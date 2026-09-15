using System.Net;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Ui;

public class UiPagesTests : IDisposable
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
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=test\n", Size = 12, Hash = "h" }
            }
        };
    }

    private async Task SeedAsync(string name, SecurityStatus status, IReadOnlyList<SecurityFinding>? findings = null)
    {
        await _factory.Repository.InsertSeedAsync(Doc(name), SeedRevision(name));
        await _factory.SecurityRepository.MarkPendingAsync(name, "rev-1", true,
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        if (status == SecurityStatus.Pending) return;

        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.CompleteScanAsync(
            name, "rev-1", "test", new ScanResult(status, findings ?? []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
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
        await _factory.SecurityRepository.MarkPendingAsync(name, sha, true,
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        if (status == SecurityStatus.Pending) return;

        await _factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _factory.SecurityRepository.CompleteScanAsync(
            name, sha, "test", new ScanResult(status, findings ?? []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
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

    [Fact]
    public async Task RootPageRendersCatalogWithPackages()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Multiple(() =>
        {
            Assert.Contains("3 packages", body);
            Assert.Contains("href=\"/package/portable-kit\"", body);
            Assert.Contains("shelly-bin", body);
            Assert.Contains("portable-pro", body);
            Assert.Contains("type=\"submit\"", body);
            Assert.Contains(">Search</button>", body);
        });
    }

    [Fact]
    public async Task RootPageSearchFormCannotNavigateNatively()
    {
        var body = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();

        // @onsubmit:preventDefault is a marker only the circuit honors, so until the WebSocket
        // connects Enter submits the form for real and the browser cannot be stopped from
        // navigating. A GET submission replaces the whole query string, so target the current
        // document (action="", which Blazor would otherwise render as action="/") and carry the
        // query in the parameter the page binds ([SupplyParameterFromQuery(Name = "q")]), so the
        // early submit runs a search instead of resetting to an empty catalog.
        Assert.Multiple(() =>
        {
            Assert.Contains("<form class=\"flex min-w-0 gap-2 w-full lg:flex-1 lg:max-w-135\" action=\"\"", body);
            var formStart = body.IndexOf("<form", StringComparison.Ordinal);
            var form = body[formStart..body.IndexOf("</form>", StringComparison.Ordinal)];
            Assert.Contains("name=\"q\"", form);
            // The embedded reset control has to stay type="button", and Search has to remain the
            // form's only submit control, or clicking it would navigate for real as well.
            Assert.Contains("<button type=\"button\"", form);
            Assert.Equal(2, form.Split("type=\"submit\"", StringSplitOptions.None).Length);
        });
    }

    [Fact]
    public async Task RootPageRendersPaginationFooter()
    {
        var body = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("Page 1 of 1", body);
        Assert.Contains("showing 1-3 of 3", body);
        Assert.Contains("aria-label=\"Pagination\"", body);
        // A single-page result disables both pagination buttons in the prerendered HTML.
        Assert.Contains("<button type=\"button\" class=\"btn\" disabled", body);
        // The page-number strip marks the active page for assistive tech.
        Assert.Contains("aria-current=\"page\"", body);
    }

    [Fact]
    public async Task RootPageCompressesResponseWithGzip()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task RootPageCompressesResponseWithBrotli()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.AcceptEncoding.ParseAdd("br");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("br", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task RootPageDecoratesRowsWithBadges()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);
        await SeedAsync("portable-pro", SecurityStatus.Flagged);

        var body = await (await _client.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Multiple(() =>
        {
            // Unseeded catalog rows are index-only.
            Assert.Contains("badge-pending", body);
            Assert.Contains("Index-only", body);
            // A non-verified head status is surfaced; a verified seeded row stays clean.
            Assert.Contains("badge-flagged", body);
            Assert.DoesNotContain("badge-verified", body);
        });
    }

    [Fact]
    public async Task PackageDetailsRenderMetadataForKnownUnseededPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Handheld gaming toolkit 1337 i3", body);
            Assert.Contains("Metadata", body);
            Assert.Contains("not seeded yet", body);
            Assert.Contains("Seed from AUR", body);
        });
    }

    [Fact]
    public async Task Mutations_disabled_hides_seed_button_for_unseeded_package()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        var response = await client.GetAsync("/package/portable-kit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("Seed from AUR", body);
        // The read-only AUR link remains available.
        Assert.Contains("href=\"https://aur.archlinux.org/packages/portable-kit\"", body);
    }

    [Fact]
    public async Task Mutations_disabled_hides_rescan_button_for_seeded_package()
    {
        await using var disabled = new SecurityTestFactory { MutationsEnabled = false };
        using var client = disabled.CreateClient();

        await disabled.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"));
        await disabled.SecurityRepository.MarkPendingAsync("shelly-bin", "rev-1", true,
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await disabled.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await disabled.SecurityRepository.CompleteScanAsync(
            "shelly-bin", "rev-1", "test", new ScanResult(SecurityStatus.Verified, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);

        var response = await client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("badge-seeded", body);
        Assert.DoesNotContain("Rescan", body);
        // Content is still served when verified; only the mutation button is hidden.
        Assert.Contains("git clone", body);
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

        var response = await _client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("shelly install aur shelly-bin --aur-url http://localhost:5290", body);
            Assert.Contains("git clone http://localhost:5290/packages/shelly-bin.git", body);
            Assert.Contains("https://www.seafoam-labs.org/shelly-alpm/docs/config/", body);
            Assert.Contains("badge-verified", body);
            Assert.Contains("long-line", body);
            Assert.Contains("Rescan", body);
        });
    }

    [Fact]
    public async Task PackageDetailsRendersCustomExternalBaseUrlInCloneBlock()
    {
        using var factory = new SecurityTestFactory { ExternalBaseUrl = "https://atoll.example.com" };
        using var client = factory.CreateClient();
        await factory.Repository.InsertSeedAsync(Doc("shelly-bin"), SeedRevision("shelly-bin"));
        await factory.SecurityRepository.MarkPendingAsync("shelly-bin", "rev-1", true,
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await factory.SecurityRepository.TryClaimPendingScanAsync("test", TimeSpan.FromMinutes(1),
            PkgBuildSecurityScanner.CurrentPolicyVersion);
        await factory.SecurityRepository.CompleteScanAsync(
            "shelly-bin", "rev-1", "test", new ScanResult(SecurityStatus.Verified, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);

        var response = await client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("shelly install aur shelly-bin --aur-url https://atoll.example.com", body);
            Assert.Contains("git clone https://atoll.example.com/packages/shelly-bin.git", body);
        });
    }

    [Fact]
    public async Task PackageDetailsRenderBlockedBannerWhenFlagged()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Flagged", body);
            Assert.Contains("gated", body);
            Assert.DoesNotContain("/packages/shelly-bin.git", body);
            Assert.DoesNotContain("shelly install aur", body);
        });
    }

    [Fact]
    public async Task UnknownPackageReturnsNotFound()
    {
        var response = await _client.GetAsync("/package/no-such-package");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRouteReturnsNotFound()
    {
        var response = await _client.GetAsync("/some/unknown/route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PackageDetailsRenderTabLinks()
    {
        var response = await _client.GetAsync("/package/portable-kit");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("href=\"/package/portable-kit/revisions\"", body);
            Assert.Contains("href=\"/package/portable-kit/files\"", body);
            Assert.Contains("tab-count", body);
        });
    }

    [Fact]
    public async Task RevisionsTabRendersHistoryRowsWithBadges()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/revisions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("rev-list", body);
            Assert.Contains("sync from upstream", body);
            Assert.Contains(">seed</p>", body);
            Assert.Contains("badge-verified", body);
            Assert.Contains("badge-flagged", body);
            Assert.Contains(">head</span>", body);
            Assert.Contains($"href=\"/package/shelly-bin?rev=rev-1\"", body);
            Assert.Contains($"href=\"/package/shelly-bin/files?rev=rev-2\"", body);
        });
    }

    [Fact]
    public async Task RevisionsTabShowsUnseededStateForIndexOnlyPackage()
    {
        var response = await _client.GetAsync("/package/portable-kit/revisions");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not seeded", body);
    }

    [Fact]
    public async Task FilesTabShowsWarningBannerAndFilesForFlaggedRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Flagged);

        var response = await _client.GetAsync("/package/shelly-bin/files");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Flagged", body);
            Assert.Contains("remain blocked", body);
            Assert.Contains("file-tree", body);
            Assert.Contains("PKGBUILD", body);
        });
    }

    [Fact]
    public async Task FilesTabRendersTreeAndKeepsSelectionInUrl()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("file-tree", body);
            Assert.Contains("rev=rev-2&amp;path=PKGBUILD", body);
            Assert.Contains("rev=rev-2&amp;path=.SRCINFO", body);
            // Directory-free sample keeps the root order ordinal: .SRCINFO before PKGBUILD.
            Assert.True(body.IndexOf("path=.SRCINFO", StringComparison.Ordinal)
                < body.IndexOf("path=PKGBUILD", StringComparison.Ordinal));
            Assert.Contains("Pick a file to preview", body);
        });
    }

    [Fact]
    public async Task FilesTabRendersSelectedFileContent()
    {
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Verified, SecurityStatus.Verified);

        var head = await _client.GetAsync("/package/shelly-bin/files?path=PKGBUILD");
        var headBody = await head.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("code-view", headBody);
            Assert.Contains("language-pkgbuild", headBody);
            Assert.Contains("pkgname=new", headBody);
            Assert.Contains("PKGBUILD", headBody);
        });

        var pinned = await _client.GetAsync("/package/shelly-bin/files?rev=rev-1&path=PKGBUILD");
        var pinnedBody = await pinned.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        Assert.Contains("pkgname=old", pinnedBody);
    }

    [Fact]
    public async Task FilesTabFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?rev=garbage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Revision not found", body);
            Assert.Contains("PKGBUILD", body);
        });
    }

    [Fact]
    public async Task FilesTabMarksMissingPathAsNotFound()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin/files?path=nope.txt");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("File not found", body);
    }

    [Fact]
    public async Task PackageOverviewPinsToRevisionAndShowsItsScan()
    {
        var findings = new[]
        {
            new SecurityFinding("evil-curl", FindingSeverity.High, "pipes curl into sh", "", "PKGBUILD")
        };
        await SeedTwoRevisionsAsync("shelly-bin", SecurityStatus.Flagged, SecurityStatus.Verified, findings);

        var response = await _client.GetAsync("/package/shelly-bin?rev=rev-1");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            // Head is verified, so the page renders; the pinned revision's own scan and findings show instead.
            Assert.Contains("evil-curl", body);
            Assert.Contains("(not head)", body);
            Assert.Contains("Revision findings", body);
            Assert.Contains("href=\"/package/shelly-bin/files?rev=rev-1\"", body);
            Assert.Contains("back to head", body);
        });
    }

    [Fact]
    public async Task PackageOverviewFallsBackToHeadForUnknownRevision()
    {
        await SeedAsync("shelly-bin", SecurityStatus.Verified);

        var response = await _client.GetAsync("/package/shelly-bin?rev=garbage");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("Revision not found", body);
            Assert.Contains("Metadata", body);
        });
    }

    [Fact]
    public async Task UnknownPackageOnPhase2TabsReturnsNotFound()
    {
        var revisions = await _client.GetAsync("/package/no-such-package/revisions");
        var files = await _client.GetAsync("/package/no-such-package/files");

        Assert.Equal(HttpStatusCode.NotFound, revisions.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);
    }
}
