using System.Net;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Ui;

public class UiPagesTests
{
    private HttpClient _client = null!;
    private SecurityTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static PackageDocument Doc(string name)
    {
        return new PackageDocument
        {
            Id = name,
            PackageName = name,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            HeadRevisionId = "rev-1",
            Revisions =
            [
                new PackageRevisionDocument
                {
                    RevisionId = "rev-1",
                    CreatedAt = DateTimeOffset.UtcNow,
                    Author = "test",
                    Message = "seed"
                }
            ]
        };
    }

    private static PackageRevisionContentDocument SeedRevision(string name)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, "rev-1"),
            PackageName = name,
            RevisionId = "rev-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            }
        };
    }

    private async Task SeedAsync(string name, SecurityStatus status, IReadOnlyList<SecurityFinding>? findings = null)
    {
        await _factory.Repository.InsertSeedAsync(Doc(name), SeedRevision(name));
        await _factory.SecurityRepository.MarkPendingAsync(name, "rev-1", true);
        if (status == SecurityStatus.Pending) return;

        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
        await _factory.SecurityRepository.CompleteScanAsync(
            name, "rev-1", "test", new ScanResult(status, findings ?? []));
    }

    [Test]
    public async Task RootPageRendersCatalogWithPackages()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("3 packages"));
            Assert.That(body, Does.Contain("href=\"/package/portable-kit\""));
            Assert.That(body, Does.Contain("shelly-bin"));
            Assert.That(body, Does.Contain("portable-pro"));
            Assert.That(body, Does.Contain("type=\"submit\""));
            Assert.That(body, Does.Contain(">Search</button>"));
        });
    }

    [Test]
    public async Task RootPageDecoratesSeededRowsWithBadges()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var body = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("badge-seeded"));
            Assert.That(body, Does.Contain("badge-verified"));
        });
    }

    [Test]
    public async Task PackageDetailsRenderMetadataForKnownUnseededPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Handheld gaming toolkit 1337 i3"));
            Assert.That(body, Does.Contain("Metadata"));
            Assert.That(body, Does.Contain("not seeded yet"));
            Assert.That(body, Does.Contain("Seed from AUR"));
        });
    }

    [Test]
    public async Task PackageDetailsRenderCloneBlockAndFindingsWhenSeededAndVerified()
    {
        var findings = new[]
        {
            new SecurityFinding(
                "long-line", FindingSeverity.Medium, "line longer than 4096 characters", "", "PKGBUILD")
        };
        await SeedAsync("shelly-bin", SecurityStatus.Verified, findings);

        var response = await _client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("git clone http://localhost:5290/packages/shelly-bin.git"));
            Assert.That(body, Does.Contain("badge-verified"));
            Assert.That(body, Does.Contain("long-line"));
            Assert.That(body, Does.Contain("Rescan"));
        });
    }

    [Test]
    public async Task PackageDetailsRenderBlockedBannerWhenFlagged()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Flagged"));
            Assert.That(body, Does.Contain("gated"));
            Assert.That(body, Does.Not.Contain("/packages/shelly-bin.git"));
        });
    }

    [Test]
    public async Task UnknownPackageReturnsNotFound()
    {
        var response = await _client.GetAsync("/package/no-such-package");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task UnknownRouteReturnsNotFound()
    {
        var response = await _client.GetAsync("/some/unknown/route");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
