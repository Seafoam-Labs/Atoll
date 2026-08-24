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

    /// <summary>
    ///     Seeds two revisions with distinct PKGBUILD content so pinned views are distinguishable,
    /// completing each revision's scan with the given status (mirrors the append-then-promote flow).
    /// </summary>
    private async Task SeedTwoRevisionsAsync(
        string name,
        SecurityStatus oldStatus,
        SecurityStatus headStatus,
        IReadOnlyList<SecurityFinding>? oldFindings = null)
    {
        await _factory.Repository.InsertSeedAsync(
            Doc(name),
            RevisionContent(name, "rev-1", "seeded from AUR", "pkgname=old\n"));
        await CompleteScanAsync(name, "rev-1", oldStatus, oldFindings);

        await _factory.Repository.AppendRevisionAsync(
            name,
            RevisionContent(name, "rev-2", "sync from upstream", "pkgname=new\n"),
            maxRevisions: 10);
        await _factory.SecurityRepository.PromoteHeadAsync(name, "rev-2");
        await CompleteScanAsync(name, "rev-2", headStatus);
    }

    private async Task CompleteScanAsync(
        string name, string sha, SecurityStatus status, IReadOnlyList<SecurityFinding>? findings = null)
    {
        await _factory.SecurityRepository.MarkPendingAsync(name, sha, true);
        if (status == SecurityStatus.Pending) return;

        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1));
        await _factory.SecurityRepository.CompleteScanAsync(
            name, sha, "test", new ScanResult(status, findings ?? []));
    }

    private static PackageRevisionContentDocument RevisionContent(
        string name, string sha, string message, string pkgbuild)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId(name, sha),
            PackageName = name,
            RevisionId = sha,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = message,
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = pkgbuild, Size = pkgbuild.Length, Hash = "h" },
                [".SRCINFO"] = new() { Content = "pkgname = test\n", Size = 15, Hash = "h" }
            }
        };
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
    public async Task RootPageRendersPaginationFooter()
    {
        var body = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.That(body, Does.Contain("Page 1 of 1"));
        Assert.That(body, Does.Contain("showing 1-3 of 3"));
        Assert.That(body, Does.Contain("aria-label=\"Pagination\""));
        // A single-page result disables both pagination buttons in the prerendered HTML.
        Assert.That(body, Does.Contain("<button type=\"button\" class=\"btn\" disabled"));
        // The page-number strip marks the active page for assistive tech.
        Assert.That(body, Does.Contain("aria-current=\"page\""));
    }

    [Test]
    public async Task RootPageCompressesResponseWithGzip()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentEncoding, Contains.Item("gzip"));
    }

    [Test]
    public async Task RootPageCompressesResponseWithBrotli()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("br");

        using var response = await _client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentEncoding, Contains.Item("br"));
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

    [Test]
    public async Task PackageDetailsRenderTabLinks()
    {
        var response = await _client.GetAsync("/package/portable-kit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("href=\"/package/portable-kit/revisions\""));
            Assert.That(body, Does.Contain("href=\"/package/portable-kit/files\""));
            Assert.That(body, Does.Contain("tab-count"));
        });
    }

    [Test]
    public async Task RevisionsTabRendersHistoryRowsWithBadges()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/revisions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("rev-list"));
            Assert.That(body, Does.Contain("sync from upstream"));
            Assert.That(body, Does.Contain(">seed</p>"));
            Assert.That(body, Does.Contain("badge-verified"));
            Assert.That(body, Does.Contain("badge-flagged"));
            Assert.That(body, Does.Contain(">head</span>"));
            Assert.That(body, Does.Contain($"href=\"/package/shelly-bin?rev=rev-1\""));
            Assert.That(body, Does.Contain($"href=\"/package/shelly-bin/files?rev=rev-2\""));
        });
    }

    [Test]
    public async Task RevisionsTabShowsUnseededStateForIndexOnlyPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit/revisions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Does.Contain("not seeded"));
    }

    [Test]
    public async Task FilesTabShowsWarningBannerAndFilesForFlaggedRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin/files");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Flagged"));
            Assert.That(body, Does.Contain("remain blocked"));
            Assert.That(body, Does.Contain("file-tree"));
            Assert.That(body, Does.Contain("PKGBUILD"));
        });
    }

    [Test]
    public async Task FilesTabRendersTreeAndKeepsSelectionInUrl()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("file-tree"));
            Assert.That(body, Does.Contain("rev=rev-2&amp;path=PKGBUILD"));
            Assert.That(body, Does.Contain("rev=rev-2&amp;path=.SRCINFO"));
            // Directory-free sample keeps the root order ordinal: .SRCINFO before PKGBUILD.
            Assert.That(body.IndexOf("path=.SRCINFO", StringComparison.Ordinal),
                Is.LessThan(body.IndexOf("path=PKGBUILD", StringComparison.Ordinal)));
            Assert.That(body, Does.Contain("Pick a file to preview"));
        });
    }

    [Test]
    public async Task FilesTabRendersSelectedFileContent()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var head = await _client.GetAsync("/package/shelly-bin/files?path=PKGBUILD");
        var headBody = await head.Content.ReadAsStringAsync();

        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(headBody, Does.Contain("code-view"));
            Assert.That(headBody, Does.Contain("pkgname=new"));
            Assert.That(headBody, Does.Contain("PKGBUILD"));
        });

        var pinned = await _client.GetAsync("/package/shelly-bin/files?rev=rev-1&path=PKGBUILD");
        var pinnedBody = await pinned.Content.ReadAsStringAsync();

        Assert.That(pinned.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(pinnedBody, Does.Contain("pkgname=old"));
    }

    [Test]
    public async Task FilesTabFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?rev=garbage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Revision not found"));
            Assert.That(body, Does.Contain("PKGBUILD"));
        });
    }

    [Test]
    public async Task FilesTabMarksMissingPathAsNotFound()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?path=nope.txt");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(body, Does.Contain("File not found"));
    }

    [Test]
    public async Task PackageOverviewPinsToRevisionAndShowsItsScan()
    {
        var findings = new[]
        {
            new SecurityFinding("evil-curl", FindingSeverity.High, "pipes curl into sh", "", "PKGBUILD")
        };
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified, findings);

        var response = await _client.GetAsync("/package/shelly-bin?rev=rev-1");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            // Head is verified, so the page renders; the pinned revision's own scan and findings show instead.
            Assert.That(body, Does.Contain("evil-curl"));
            Assert.That(body, Does.Contain("(not head)"));
            Assert.That(body, Does.Contain("Revision findings"));
            Assert.That(body, Does.Contain("href=\"/package/shelly-bin/files?rev=rev-1\""));
            Assert.That(body, Does.Contain("back to head"));
        });
    }

    [Test]
    public async Task PackageOverviewFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin?rev=garbage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("Revision not found"));
            Assert.That(body, Does.Contain("Metadata"));
        });
    }

    [Test]
    public async Task UnknownPackageOnPhase2TabsReturnsNotFound()
    {
        var revisions = await _client.GetAsync("/package/no-such-package/revisions");
        var files = await _client.GetAsync("/package/no-such-package/files");

        Assert.That(revisions.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(files.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }
}
