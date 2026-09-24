using System.Net;
using System.Text.RegularExpressions;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

public sealed partial class UiPagesTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public UiPagesTests()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
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
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            }
        };
    }

    private async Task SeedAsync(string name, SecurityStatus status, params SecurityFinding[] findings)
    {
        await _factory.Repository.InsertSeedAsync(Doc(name), SeedRevision(name), TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.ScanRevisionAsync(name, "rev-1", status, findings);
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
            RevisionContent(name, "rev-1", "seeded from AUR", "pkgname=old\n"),
            TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.ScanRevisionAsync(
            name, "rev-1", oldStatus, findings: oldFindings?.ToArray() ?? []);

        await _factory.Repository.AppendRevisionAsync(
            name,
            RevisionContent(name, "rev-2", "sync from upstream", "pkgname=new\n"),
            maxRevisions: 10,
            ct: TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.PromoteHeadAsync(name, "rev-2", TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.ScanRevisionAsync(name, "rev-2", headStatus);
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
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = new() { Content = pkgbuild, Size = pkgbuild.Length, Hash = "h" },
                [".SRCINFO"] = new() { Content = "pkgname = test\n", Size = 15, Hash = "h" }
            }
        };
    }

    [Fact]
    public async Task RootPageRendersCatalogWithPackages()
    {
        var response = await _client.GetAsync("/", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Multiple(() =>
        {
            Assert.Contains("3 packages", body, StringComparison.Ordinal);
            Assert.Contains("href=\"/package/portable-kit\"", body, StringComparison.Ordinal);
            Assert.Contains("shelly-bin", body, StringComparison.Ordinal);
            Assert.Contains("portable-pro", body, StringComparison.Ordinal);
            Assert.Contains("type=\"submit\"", body, StringComparison.Ordinal);
            Assert.Contains(">Search</button>", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RootPageSearchFormCannotNavigateNatively()
    {
        var body = await (await _client.GetAsync("/", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // @onsubmit:preventDefault is a marker only the circuit honors, so until the WebSocket
        // connects Enter submits the form for real and the browser cannot be stopped from
        // navigating. A GET submission replaces the whole query string, so target the current
        // document (action="", which Blazor would otherwise render as action="/") and carry the
        // query in the parameter the page binds ([SupplyParameterFromQuery(Name = "q")]), so the
        // early submit runs a search instead of resetting to an empty catalog.
        Assert.Multiple(() =>
        {
            Assert.Contains("<form class=\"flex min-w-0 gap-2 w-full lg:flex-1 lg:max-w-135\" action=\"\"", body, StringComparison.Ordinal);
            var formStart = body.IndexOf("<form", StringComparison.Ordinal);
            var form = body[formStart..body.IndexOf("</form>", StringComparison.Ordinal)];
            Assert.Contains("name=\"q\"", form, StringComparison.Ordinal);
            // The embedded reset control has to stay type="button", and Search has to remain the
            // form's only submit control, or clicking it would navigate for real as well.
            Assert.Contains("<button type=\"button\"", form, StringComparison.Ordinal);
            Assert.Equal(2, form.Split("type=\"submit\"", StringSplitOptions.None).Length);
        });
    }

    [Fact]
    public async Task RootPageRendersPaginationFooter()
    {
        var body = await (await _client.GetAsync("/", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Page 1 of 1", body, StringComparison.Ordinal);
        Assert.Contains("showing 1-3 of 3", body, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Pagination\"", body, StringComparison.Ordinal);
        // A single-page result disables both pagination buttons in the prerendered HTML.
        Assert.Contains("<button type=\"button\" class=\"btn\" disabled", body, StringComparison.Ordinal);
        // The page-number strip marks the active page for assistive tech.
        Assert.Contains("aria-current=\"page\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RootPageCompressesResponseWithGzip()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RootPageCompressesResponseWithBrotli()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("br");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RootPageCompressesResponseOverForwardedHttps()
    {
        // The deployed proxy terminates TLS and forwards the https scheme, so the app observes
        // HTTPS on every request and the framework's EnableForHttps default would silently
        // disable compression there.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding, StringComparer.Ordinal);
    }

    [Fact]
    public async Task RootPageDecoratesRowsWithBadges()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);
        await SeedAsync("portable-pro", SecurityStatus.Flagged);

        var body = await (await _client.GetAsync("/", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // Unseeded catalog rows are index-only.
            Assert.Contains("badge-pending", body, StringComparison.Ordinal);
            Assert.Contains("Index-only", body, StringComparison.Ordinal);
            // A non-verified head status is surfaced; a verified seeded row stays clean.
            Assert.Contains("badge-flagged", body, StringComparison.Ordinal);
            Assert.DoesNotContain("badge-verified", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageDetailsRenderMetadataForKnownUnseededPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Handheld gaming toolkit 1337 i3", body, StringComparison.Ordinal);
            Assert.Contains("Metadata", body, StringComparison.Ordinal);
            Assert.Contains("not seeded yet", body, StringComparison.Ordinal);
            Assert.Contains("Seed from AUR", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Mutations_disabled_hides_seed_button_for_unseeded_package()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        var response = await client.GetAsync("/package/portable-kit", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Seed from AUR", body, StringComparison.Ordinal);
        // The read-only AUR link remains available.
        Assert.Contains("href=\"https://aur.archlinux.org/packages/portable-kit\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutations_disabled_hides_rescan_button_for_seeded_package()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        await disabled.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"), TestContext.Current.CancellationToken);
        await disabled.SecurityRepository.ScanRevisionAsync("shelly-bin", "rev-1", SecurityStatus.Verified);

        var response = await client.GetAsync("/package/shelly-bin", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("badge-seeded", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Rescan", body, StringComparison.Ordinal);
        // Content is still served when verified; only the mutation button is hidden.
        Assert.Contains("git clone", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageDetailsRenderCloneBlockAndFindingsWhenSeededAndVerified()
    {
        var findings = new[]
        {
            new SecurityFinding(
                "long-line", FindingSeverity.Medium, "line longer than 4096 characters", "", "PKGBUILD")
        };
        await SeedAsync("shelly-bin", SecurityStatus.Verified, findings);

        var response = await _client.GetAsync("/package/shelly-bin", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("shelly install aur shelly-bin --aur-url http://localhost:5290", body, StringComparison.Ordinal);
            Assert.Contains("git clone http://localhost:5290/packages/shelly-bin.git", body, StringComparison.Ordinal);
            Assert.Contains("https://www.seafoam-labs.org/shelly-alpm/docs/config/", body, StringComparison.Ordinal);
            Assert.Contains("badge-verified", body, StringComparison.Ordinal);
            Assert.Contains("long-line", body, StringComparison.Ordinal);
            Assert.Contains("Rescan", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageDetailsRendersCustomExternalBaseUrlInCloneBlock()
    {
        await using var factory = new SecurityTestFactory { ExternalBaseUrl = "https://atoll.example.com" };
        using var client = factory.CreateClient();
        await factory.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"), TestContext.Current.CancellationToken);
        await factory.SecurityRepository.ScanRevisionAsync("shelly-bin", "rev-1", SecurityStatus.Verified);

        var response = await client.GetAsync("/package/shelly-bin", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("shelly install aur shelly-bin --aur-url https://atoll.example.com", body, StringComparison.Ordinal);
            Assert.Contains("git clone https://atoll.example.com/packages/shelly-bin.git", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageDetailsTrimsTrailingSlashFromExternalBaseUrlInCloneBlock()
    {
        await using var factory = new SecurityTestFactory { ExternalBaseUrl = "https://atoll.example.com/" };
        using var client = factory.CreateClient();
        await factory.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"), TestContext.Current.CancellationToken);
        await factory.SecurityRepository.ScanRevisionAsync("shelly-bin", "rev-1", SecurityStatus.Verified);

        var response = await client.GetAsync("/package/shelly-bin", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("shelly install aur shelly-bin --aur-url https://atoll.example.com", body, StringComparison.Ordinal);
            Assert.Contains("git clone https://atoll.example.com/packages/shelly-bin.git", body, StringComparison.Ordinal);
            Assert.DoesNotContain("//packages", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageDetailsRenderBlockedBannerWhenFlagged()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Flagged", body, StringComparison.Ordinal);
            Assert.Contains("gated", body, StringComparison.Ordinal);
            Assert.DoesNotContain("/packages/shelly-bin.git", body, StringComparison.Ordinal);
            Assert.DoesNotContain("shelly install aur", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnknownPackageReturnsNotFound()
    {
        var response = await _client.GetAsync("/package/no-such-package", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRouteReturnsNotFound()
    {
        var response = await _client.GetAsync("/some/unknown/route", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PackageDetailsRenderTabLinks()
    {
        var response = await _client.GetAsync("/package/portable-kit", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("href=\"/package/portable-kit/revisions\"", body, StringComparison.Ordinal);
            Assert.Contains("href=\"/package/portable-kit/files\"", body, StringComparison.Ordinal);
            Assert.Contains("tab-count", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RevisionsTabRendersHistoryRowsWithBadges()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/revisions", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("rev-list", body, StringComparison.Ordinal);
            Assert.Contains("sync from upstream", body, StringComparison.Ordinal);
            Assert.Contains(">seed</p>", body, StringComparison.Ordinal);
            Assert.Contains("badge-verified", body, StringComparison.Ordinal);
            Assert.Contains("badge-flagged", body, StringComparison.Ordinal);
            Assert.Contains(">head</span>", body, StringComparison.Ordinal);
            Assert.Contains("href=\"/package/shelly-bin?rev=rev-1\"", body, StringComparison.Ordinal);
            Assert.Contains("href=\"/package/shelly-bin/files?rev=rev-2\"", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RevisionsTabShowsUnseededStateForIndexOnlyPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit/revisions", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not seeded", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesTabShowsWarningBannerAndFilesForFlaggedRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin/files", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Flagged", body, StringComparison.Ordinal);
            Assert.Contains("remain blocked", body, StringComparison.Ordinal);
            Assert.Contains("file-tree", body, StringComparison.Ordinal);
            Assert.Contains("PKGBUILD", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FilesTabRendersTreeAndKeepsSelectionInUrl()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("file-tree", body, StringComparison.Ordinal);
            Assert.Contains("rev=rev-2&amp;path=PKGBUILD", body, StringComparison.Ordinal);
            Assert.Contains("rev=rev-2&amp;path=.SRCINFO", body, StringComparison.Ordinal);
            // Directory-free sample keeps the root order ordinal: .SRCINFO before PKGBUILD.
            Assert.True(body.IndexOf("path=.SRCINFO", StringComparison.Ordinal)
                < body.IndexOf("path=PKGBUILD", StringComparison.Ordinal));
            Assert.Contains("Pick a file to preview", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FilesTabRendersSelectedFileContent()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var head = await _client.GetAsync("/package/shelly-bin/files?path=PKGBUILD", TestContext.Current.CancellationToken);
        var headBody = await head.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("code-view", headBody, StringComparison.Ordinal);
            Assert.Contains("language-pkgbuild", headBody, StringComparison.Ordinal);
            Assert.Contains("pkgname=new", headBody, StringComparison.Ordinal);
            Assert.Contains("PKGBUILD", headBody, StringComparison.Ordinal);
        });

        var pinned = await _client.GetAsync("/package/shelly-bin/files?rev=rev-1&path=PKGBUILD", TestContext.Current.CancellationToken);
        var pinnedBody = await pinned.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        Assert.Contains("pkgname=old", pinnedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesTabFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?rev=garbage", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Revision not found", body, StringComparison.Ordinal);
            Assert.Contains("PKGBUILD", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FilesTabMarksMissingPathAsNotFound()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?path=nope.txt", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("File not found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageOverviewPinsToRevisionAndShowsItsScan()
    {
        var findings = new[]
        {
            new SecurityFinding("evil-curl", FindingSeverity.High, "pipes curl into sh", "", "PKGBUILD")
        };
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified, findings);

        var response = await _client.GetAsync("/package/shelly-bin?rev=rev-1", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            // Head is verified, so the page renders; the pinned revision's own scan and findings show instead.
            Assert.Contains("evil-curl", body, StringComparison.Ordinal);
            Assert.Contains("(not head)", body, StringComparison.Ordinal);
            Assert.Contains("Revision findings", body, StringComparison.Ordinal);
            Assert.Contains("href=\"/package/shelly-bin/files?rev=rev-1\"", body, StringComparison.Ordinal);
            Assert.Contains("back to head", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackageOverviewFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin?rev=garbage", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Revision not found", body, StringComparison.Ordinal);
            Assert.Contains("Metadata", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnknownPackageOnPhase2TabsReturnsNotFound()
    {
        var revisions = await _client.GetAsync("/package/no-such-package/revisions", TestContext.Current.CancellationToken);
        var files = await _client.GetAsync("/package/no-such-package/files", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, revisions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);
    }

    private async Task<string> GetBodyAsync(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("/", "Packages - Atoll")]
    [InlineData("/status", "Status - Atoll")]
    [InlineData("/package/shelly-bin", "shelly-bin - Atoll")]
    [InlineData("/package/shelly-bin/files", "Files - shelly-bin - Atoll")]
    [InlineData("/package/shelly-bin/revisions", "Revisions - shelly-bin - Atoll")]
    [InlineData("/not-found", "Not found - Atoll")]
    [InlineData("/some/unknown/route", "Not found - Atoll")]
    [InlineData("/package/no-such-package", "Not found - Atoll")]
    public async Task EveryRouteServesExactlyOneTitle(string path, string expectedTitle)
    {
        var body = await GetBodyAsync(path);

        Assert.Multiple(() =>
        {
            Assert.Single(TitleTag.Matches(body));
            Assert.Contains(expectedTitle, body, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("/", "self-hosted Arch User Repository (AUR) mirror")]
    [InlineData("/status", "Live status of the Atoll AUR mirror")]
    [InlineData("/package/shelly-bin", "Shelly: A Modern Arch Package Manager (prebuilt binary)")]
    [InlineData("/package/shelly-bin/files", "Shelly: A Modern Arch Package Manager (prebuilt binary)")]
    [InlineData("/package/shelly-bin/revisions", "Shelly: A Modern Arch Package Manager (prebuilt binary)")]
    [InlineData("/not-found", "Nothing lives at this address")]
    public async Task EveryRouteServesAMetaDescription(string path, string expectedDescription)
    {
        var body = await GetBodyAsync(path);

        Assert.Multiple(() =>
        {
            Assert.Contains("name=\"description\"", body, StringComparison.Ordinal);
            Assert.Contains(expectedDescription, body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PackagePagesServeOpenGraphAndCanonicalFromExternalBaseUrl()
    {
        await using var factory = new SecurityTestFactory { ExternalBaseUrl = "https://atoll.example.com/" };
        using var client = factory.CreateClient();

        var routes = new[]
        {
            "/package/shelly-bin",
            "/package/shelly-bin/files",
            "/package/shelly-bin/revisions"
        };
        foreach (var path in routes)
        {
            var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            var canonical = $"https://atoll.example.com{path}";
            Assert.Multiple(() =>
            {
                Assert.Contains("property=\"og:title\"", body, StringComparison.Ordinal);
                Assert.Contains("property=\"og:description\"", body, StringComparison.Ordinal);
                Assert.Contains($"property=\"og:url\" content=\"{canonical}\"", body, StringComparison.Ordinal);
                Assert.Contains($"rel=\"canonical\" href=\"{canonical}\"", body, StringComparison.Ordinal);
            });
        }
    }

    [GeneratedRegex("<title>", RegexOptions.None, 250)]
    private static partial Regex TitleTag { get; }
}
