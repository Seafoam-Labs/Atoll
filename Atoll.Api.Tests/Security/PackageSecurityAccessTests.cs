using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityAccessTests
{
    private static async Task SeedPackageAsync(InMemoryPackageRepository packages)
    {
        await packages.InsertSeedAsync(new PackageDocument
        {
            Id = "pkg",
            PackageName = "pkg",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadRevisionId = "rev-1"
        });
    }

    private static PackageSecurityAccess Create(
        InMemoryPackageRepository packages,
        InMemoryPackageSecurityRepository security,
        bool enabled = true)
    {
        var options = Options.Create(new AtollOptions { Security = new SecurityOptions { Enabled = enabled } });
        return new PackageSecurityAccess(packages, security, options);
    }

    [TestCase(SecurityStatus.Verified, true, null)]
    [TestCase(SecurityStatus.Pending, false, SecurityAccessReasonCodes.Pending)]
    [TestCase(SecurityStatus.Flagged, false, SecurityAccessReasonCodes.Flagged)]
    [TestCase(SecurityStatus.Error, false, SecurityAccessReasonCodes.Error)]
    public async Task Status_is_enforced(SecurityStatus status, bool allowed, string? reason)
    {
        var packages = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        await SeedPackageAsync(packages);
        await security.MarkPendingAsync("pkg", "rev-1", true);
        if (status != SecurityStatus.Pending)
        {
            var result = new ScanResult(status, []);
            _ = await security.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
            await security.CompleteScanAsync("pkg", "rev-1", "test", result);
        }

        var access = Create(packages, security);
        var result1 = await access.CheckAsync("pkg");

        Assert.That(result1.Allowed, Is.EqualTo(allowed));
        Assert.That(result1.ReasonCode, Is.EqualTo(reason));
    }

    [Test]
    public async Task Missing_scan_is_pending_and_blocked()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);

        var result = await Create(packages, new InMemoryPackageSecurityRepository()).CheckAsync("pkg");

        Assert.That(result.Allowed, Is.False);
        Assert.That(result.ReasonCode, Is.EqualTo(SecurityAccessReasonCodes.Pending));
    }

    [Test]
    public async Task Disabled_feature_allows_everything()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);
        var security = new InMemoryPackageSecurityRepository();
        await security.MarkPendingAsync("pkg", "rev-1", true);

        var result = await Create(packages, security, false).CheckAsync("pkg");

        Assert.That(result.Allowed, Is.True);
    }

    [Test]
    public async Task Flagged_revision_blocks_only_itself()
    {
        var packages = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        await SeedPackageAsync(packages);
        await packages.AppendRevisionAsync(
            "pkg",
            new PackageRevisionDocument { RevisionId = "rev-2", CreatedAt = DateTimeOffset.UtcNow },
            new Dictionary<string, PackageFile>(),
            10);

        await security.MarkPendingAsync("pkg", "rev-1", false);
        await security.MarkPendingAsync("pkg", "rev-2", true);
        _ = await security.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
        await security.CompleteScanAsync("pkg", "rev-1", "test", new ScanResult(SecurityStatus.Verified, []));
        _ = await security.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
        await security.CompleteScanAsync("pkg", "rev-2", "test", new ScanResult(SecurityStatus.Flagged, []));

        var access = Create(packages, security);

        var flagged = await access.CheckAsync("pkg", "rev-2");
        var clean = await access.CheckAsync("pkg", "rev-1");
        var head = await access.CheckAsync("pkg");

        Assert.Multiple(() =>
        {
            Assert.That(flagged.Allowed, Is.False);
            Assert.That(flagged.ReasonCode, Is.EqualTo(SecurityAccessReasonCodes.Flagged));
            Assert.That(clean.Allowed, Is.True);
            Assert.That(head.Allowed, Is.False);
            Assert.That(head.ReasonCode, Is.EqualTo(SecurityAccessReasonCodes.Flagged));
        });
    }

    [Test]
    public async Task Unknown_revision_is_blocked_as_pending()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);

        var result = await Create(packages, new InMemoryPackageSecurityRepository()).CheckAsync("pkg", "rev-missing");

        Assert.That(result.Allowed, Is.False);
        Assert.That(result.ReasonCode, Is.EqualTo(SecurityAccessReasonCodes.Pending));
    }

    [Test]
    public async Task Unknown_package_is_allowed()
    {
        var result = await Create(new InMemoryPackageRepository(), new InMemoryPackageSecurityRepository()).CheckAsync("missing");

        Assert.That(result.Allowed, Is.True);
    }
}