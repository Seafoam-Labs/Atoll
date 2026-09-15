using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityAccessTests
{
    private static async Task SeedPackageAsync(InMemoryPackageRepository packages)
    {
        var now = DateTimeOffset.UtcNow;
        await packages.InsertSeedAsync(
            new PackageDocument
            {
                Id = "pkg",
                PackageName = "pkg",
                CreatedAt = now,
                UpdatedAt = now,
                HeadRevisionId = "rev-1",
                Revisions = [new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = now }]
            },
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId("pkg", "rev-1"),
                PackageName = "pkg",
                RevisionId = "rev-1",
                CreatedAt = now
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

    [Theory]
    [InlineData(SecurityStatus.Verified, true, null)]
    [InlineData(SecurityStatus.Pending, false, SecurityAccessReasonCodes.Pending)]
    [InlineData(SecurityStatus.Flagged, false, SecurityAccessReasonCodes.Flagged)]
    [InlineData(SecurityStatus.Error, false, SecurityAccessReasonCodes.Error)]
    public async Task Status_is_enforced(SecurityStatus status, bool allowed, string? reason)
    {
        var packages = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        await SeedPackageAsync(packages);
        await security.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        if (status != SecurityStatus.Pending)
        {
            var result = new ScanResult(status, []);
            _ = await security.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
            await security.CompleteScanAsync(
                "pkg", "rev-1", "test", result, PkgBuildSecurityScanner.CurrentPolicyVersion);
        }

        var access = Create(packages, security);
        var result1 = await access.CheckAsync("pkg");

        Assert.Equal(allowed, result1.Allowed);
        Assert.Equal(reason, result1.ReasonCode);
    }

    [Fact]
    public async Task Missing_scan_is_pending_and_blocked()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);

        var result = await Create(packages, new InMemoryPackageSecurityRepository()).CheckAsync("pkg");

        Assert.False(result.Allowed);
        Assert.Equal(SecurityAccessReasonCodes.Pending, result.ReasonCode);
    }

    [Fact]
    public async Task Disabled_feature_allows_everything()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);
        var security = new InMemoryPackageSecurityRepository();
        await security.MarkPendingAsync("pkg", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);

        var result = await Create(packages, security, false).CheckAsync("pkg");

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Flagged_revision_blocks_only_itself()
    {
        var packages = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        await SeedPackageAsync(packages);
        await packages.AppendRevisionAsync(
            "pkg",
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId("pkg", "rev-2"),
                PackageName = "pkg",
                RevisionId = "rev-2",
                CreatedAt = DateTimeOffset.UtcNow,
                Files = new Dictionary<string, PackageFile>()
            },
            10);

        await security.MarkPendingAsync("pkg", "rev-1", false, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await security.MarkPendingAsync("pkg", "rev-2", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        await security.CompleteScanAsync("pkg", SecurityStatus.Verified);
        await security.CompleteScanAsync("pkg", SecurityStatus.Flagged);

        var access = Create(packages, security);

        var flagged = await access.CheckAsync("pkg", "rev-2");
        var clean = await access.CheckAsync("pkg", "rev-1");
        var head = await access.CheckAsync("pkg");

        Assert.Multiple(() =>
        {
            Assert.False(flagged.Allowed);
            Assert.Equal(SecurityAccessReasonCodes.Flagged, flagged.ReasonCode);
            Assert.True(clean.Allowed);
            Assert.False(head.Allowed);
            Assert.Equal(SecurityAccessReasonCodes.Flagged, head.ReasonCode);
        });
    }

    [Fact]
    public async Task Unknown_revision_is_blocked_as_pending()
    {
        var packages = new InMemoryPackageRepository();
        await SeedPackageAsync(packages);

        var result = await Create(packages, new InMemoryPackageSecurityRepository()).CheckAsync("pkg", "rev-missing");

        Assert.False(result.Allowed);
        Assert.Equal(SecurityAccessReasonCodes.Pending, result.ReasonCode);
    }

    [Fact]
    public async Task Unknown_package_is_allowed()
    {
        var result = await Create(new InMemoryPackageRepository(), new InMemoryPackageSecurityRepository()).CheckAsync("missing");

        Assert.True(result.Allowed);
    }
}