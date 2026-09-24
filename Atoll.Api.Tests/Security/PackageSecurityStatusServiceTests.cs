using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityStatusServiceTests
{
    private readonly InMemoryPackageRepository _packages;
    private readonly InMemoryPackageSecurityRepository _security;
    private readonly PackageSecurityStatusService _service;
    private readonly int _policyVersion;

    public PackageSecurityStatusServiceTests()
    {
        _packages = new InMemoryPackageRepository();
        _security = new InMemoryPackageSecurityRepository();
        var scanner = new PkgBuildSecurityScanner();
        _policyVersion = scanner.PolicyVersion;
        _service = new PackageSecurityStatusService(_packages, _security, scanner);
    }

    private async Task SeedAsync(string package, string head, params string[] revisions)
    {
        await _packages.InsertSeedAsync(
            new PackageDocument
            {
                Id = package,
                PackageName = package,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                HeadRevisionId = head,
                Revisions =
                [
                    .. revisions.Select(revision => new PackageRevisionDocument
                    {
                        RevisionId = revision,
                        CreatedAt = DateTimeOffset.UtcNow,
                        Author = "test",
                        Message = "seed"
                    })
                ]
            },
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(package, head),
                PackageName = package,
                RevisionId = head,
                CreatedAt = DateTimeOffset.UtcNow,
                Author = "test",
                Message = "seed",
                Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
                }
            }, TestContext.Current.CancellationToken);
    }

    private async Task QueueAsync(string package, string revision, bool isHead)
    {
        await _security.MarkPendingAsync(package, revision, isHead, _policyVersion, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetHistoryAsync_unknown_package_returns_null()
    {
        Assert.Null(await _service.GetHistoryAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetHistoryAsync_lists_head_first_then_newest_scan()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2", "rev-3");
        await QueueAsync("pkg", "rev-1", true);
        await QueueAsync("pkg", "rev-2", false);
        await QueueAsync("pkg", "rev-3", false);

        // The head is scanned first, so it carries the oldest ScannedAt: IsHead must win.
        await _security.CompleteScanAsync("pkg", SecurityStatus.Verified);
        await _security.CompleteScanAsync("pkg", SecurityStatus.Verified);
        await _security.CompleteScanAsync("pkg", SecurityStatus.Flagged,
            new SecurityFinding("dangerous-command", FindingSeverity.High, "rm -rf", "rm -rf /", "PKGBUILD"));

        var history = await _service.GetHistoryAsync("pkg", TestContext.Current.CancellationToken);

        Assert.NotNull(history);
        var tail = history!.Revisions.Skip(1).Select(r => r.ScannedAt!.Value).ToArray();

        Assert.Equivalent(new[] { "rev-1", "rev-2", "rev-3" },
            history.Revisions.Select(r => r.RevisionId), strict: true);
        Assert.Multiple(() =>
        {
            Assert.Equal("rev-1", history.HeadRevisionId);
            Assert.Equal("rev-1", history.Revisions[0].RevisionId);
            Assert.True(history.Revisions[0].IsHead);
            Assert.Equal("Verified", history.Revisions[0].Status);
            Assert.Equal(0, history.Revisions[0].FindingCount);
            Assert.Equal("rev-3", history.Revisions[1].RevisionId);
            Assert.Equal(1, history.Revisions[1].FindingCount);
            Assert.Equal(tail.OrderDescending(), tail);
        });
    }

    [Fact]
    public async Task GetRevisionAsync_unknown_package_returns_null()
    {
        Assert.Null(await _service.GetRevisionAsync("missing", "rev-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRevisionAsync_unknown_revision_returns_null()
    {
        await SeedAsync("pkg", "rev-1", "rev-1");

        Assert.Null(await _service.GetRevisionAsync("pkg", "rev-99", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRevisionAsync_reports_unscanned_revision_as_pending()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");

        var head = await _service.GetRevisionAsync("pkg", "rev-1", TestContext.Current.CancellationToken);
        var older = await _service.GetRevisionAsync("pkg", "rev-2", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.NotNull(head);
            Assert.NotNull(older);
            Assert.Equal("Pending", head!.Status);
            Assert.True(head.IsHead);
            Assert.Null(head.ScannedAt);
            Assert.Equal(0, head.FindingCount);
            Assert.False(older!.IsHead);
        });
    }

    [Fact]
    public async Task QueueRescanAsync_unknown_package_returns_null()
    {
        Assert.Null(await _service.QueueRescanAsync("missing", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueRescanAsync_unknown_revision_returns_null()
    {
        await SeedAsync("pkg", "rev-1", "rev-1");

        Assert.Null(await _service.QueueRescanAsync("pkg", "rev-99", TestContext.Current.CancellationToken));
        Assert.Null(await _security.GetHeadAsync("pkg", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task QueueRescanAsync_defaults_to_the_head_revision()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");

        var queued = await _service.QueueRescanAsync("pkg", ct: TestContext.Current.CancellationToken);
        var scan = await _security.GetAsync("pkg", "rev-1", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal("rev-1", queued);
            Assert.NotNull(scan);
            Assert.Equal(SecurityStatus.Pending, scan!.Status);
            Assert.True(scan.IsHead);
            Assert.Equal(_policyVersion, scan.RequiredPolicyVersion);
        });
    }

    [Fact]
    public async Task QueueRescanAsync_requeues_an_already_verified_revision_as_pending()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");
        await QueueAsync("pkg", "rev-2", false);
        await _security.CompleteScanAsync("pkg", SecurityStatus.Verified);
        Assert.Equal(SecurityStatus.Verified, (await _security.GetAsync("pkg", "rev-2", TestContext.Current.CancellationToken))!.Status);

        var queued = await _service.QueueRescanAsync("pkg", "rev-2", TestContext.Current.CancellationToken);
        var scan = await _security.GetAsync("pkg", "rev-2", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal("rev-2", queued);
            Assert.Equal(SecurityStatus.Pending, scan!.Status);
            Assert.False(scan.IsHead, "a non-head revision keeps its head flag");
        });
    }
}
