using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class PackageSecurityStatusServiceTests
{
    private InMemoryPackageRepository _packages = null!;
    private InMemoryPackageSecurityRepository _security = null!;
    private PackageSecurityStatusService _service = null!;
    private int _policyVersion;

    [SetUp]
    public void SetUp()
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
                Files = new Dictionary<string, PackageFile>
                {
                    ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
                }
            });
    }

    private async Task QueueAsync(string package, string revision, bool isHead)
    {
        await _security.MarkPendingAsync(package, revision, isHead, _policyVersion);
    }

    [Test]
    public async Task GetHistoryAsync_unknown_package_returns_null()
    {
        Assert.That(await _service.GetHistoryAsync("missing"), Is.Null);
    }

    [Test]
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

        var history = await _service.GetHistoryAsync("pkg");

        Assert.That(history, Is.Not.Null);
        var tail = history!.Revisions.Skip(1).Select(r => r.ScannedAt!.Value).ToArray();

        Assert.That(history.Revisions.Select(r => r.RevisionId),
            Is.EquivalentTo(new[] { "rev-1", "rev-2", "rev-3" }));
        Assert.Multiple(() =>
        {
            Assert.That(history.HeadRevisionId, Is.EqualTo("rev-1"));
            Assert.That(history.Revisions[0].RevisionId, Is.EqualTo("rev-1"));
            Assert.That(history.Revisions[0].IsHead, Is.True);
            Assert.That(history.Revisions[0].Status, Is.EqualTo("Verified"));
            Assert.That(history.Revisions[0].FindingCount, Is.Zero);
            Assert.That(history.Revisions[1].RevisionId, Is.EqualTo("rev-3"), "newest scan follows the head");
            Assert.That(history.Revisions[1].FindingCount, Is.EqualTo(1));
            Assert.That(tail, Is.EqualTo(tail.OrderByDescending(d => d)),
                "non-head revisions are ordered newest scan first");
        });
    }

    [Test]
    public async Task GetRevisionAsync_unknown_package_returns_null()
    {
        Assert.That(await _service.GetRevisionAsync("missing", "rev-1"), Is.Null);
    }

    [Test]
    public async Task GetRevisionAsync_unknown_revision_returns_null()
    {
        await SeedAsync("pkg", "rev-1", "rev-1");

        Assert.That(await _service.GetRevisionAsync("pkg", "rev-99"), Is.Null);
    }

    [Test]
    public async Task GetRevisionAsync_reports_unscanned_revision_as_pending()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");

        var head = await _service.GetRevisionAsync("pkg", "rev-1");
        var older = await _service.GetRevisionAsync("pkg", "rev-2");

        Assert.Multiple(() =>
        {
            Assert.That(head, Is.Not.Null);
            Assert.That(older, Is.Not.Null);
            Assert.That(head!.Status, Is.EqualTo("Pending"));
            Assert.That(head.IsHead, Is.True);
            Assert.That(head.ScannedAt, Is.Null);
            Assert.That(head.FindingCount, Is.Zero);
            Assert.That(older!.IsHead, Is.False);
        });
    }

    [Test]
    public async Task QueueRescanAsync_unknown_package_returns_null()
    {
        Assert.That(await _service.QueueRescanAsync("missing"), Is.Null);
    }

    [Test]
    public async Task QueueRescanAsync_unknown_revision_returns_null()
    {
        await SeedAsync("pkg", "rev-1", "rev-1");

        Assert.That(await _service.QueueRescanAsync("pkg", "rev-99"), Is.Null);
        Assert.That(await _security.GetHeadAsync("pkg"), Is.Null, "nothing is queued for an unknown revision");
    }

    [Test]
    public async Task QueueRescanAsync_defaults_to_the_head_revision()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");

        var queued = await _service.QueueRescanAsync("pkg");
        var scan = await _security.GetAsync("pkg", "rev-1");

        Assert.Multiple(() =>
        {
            Assert.That(queued, Is.EqualTo("rev-1"));
            Assert.That(scan, Is.Not.Null);
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(scan.IsHead, Is.True);
            Assert.That(scan.RequiredPolicyVersion, Is.EqualTo(_policyVersion));
        });
    }

    [Test]
    public async Task QueueRescanAsync_requeues_an_already_verified_revision_as_pending()
    {
        await SeedAsync("pkg", "rev-1", "rev-1", "rev-2");
        await QueueAsync("pkg", "rev-2", false);
        await _security.CompleteScanAsync("pkg", SecurityStatus.Verified);
        Assert.That((await _security.GetAsync("pkg", "rev-2"))!.Status, Is.EqualTo(SecurityStatus.Verified));

        var queued = await _service.QueueRescanAsync("pkg", "rev-2");
        var scan = await _security.GetAsync("pkg", "rev-2");

        Assert.Multiple(() =>
        {
            Assert.That(queued, Is.EqualTo("rev-2"));
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(scan.IsHead, Is.False, "a non-head revision keeps its head flag");
        });
    }
}
