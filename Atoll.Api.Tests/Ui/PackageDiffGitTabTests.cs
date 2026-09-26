using System.Net;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Ui;

/// <summary>
///     The diff tab end to end against the real <c>git</c> CLI. Its own factory opts back in to
///     <see cref="GitTextDiffer" />, since <see cref="SecurityTestFactory" /> stubs the differ by default.
/// </summary>
[Trait("Category", "RequiresGit")]
public sealed class PackageDiffGitTabTests : IAsyncLifetime
{
    // Nullable because InitializeAsync can skip the whole class before the host is built.
    private SecurityTestFactory? _factory;
    private HttpClient? _client;

    public async ValueTask InitializeAsync()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        Assert.SkipUnless(exitCode == 0, "git binary is required for these tests");

        _factory = new SecurityTestFactory { Differ = new GitTextDiffer() };
        _client = _factory.CreateClient();

        await _factory.Repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = "shelly-bin",
                PackageName = "shelly-bin",
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
                        Message = "seeded from AUR"
                    }
                ]
            },
            Revision("rev-1", "seeded from AUR", "pkgname=old\n"),
            CancellationToken.None);
        await _factory.SecurityRepository.ScanRevisionAsync("shelly-bin", "rev-1", SecurityStatus.Verified);

        await _factory.Repository.AppendRevisionAsync(
            "shelly-bin",
            Revision("rev-2", "sync from upstream", "pkgname=new\n"),
            maxRevisions: 10,
            ct: CancellationToken.None);
        await _factory.SecurityRepository.PromoteHeadAsync("shelly-bin", "rev-2", CancellationToken.None);
        await _factory.SecurityRepository.ScanRevisionAsync("shelly-bin", "rev-2", SecurityStatus.Verified);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    private static PackageRevisionContentDocument Revision(string sha, string message, string pkgbuild)
    {
        return new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("shelly-bin", sha),
            PackageName = "shelly-bin",
            RevisionId = sha,
            CreatedAt = DateTimeOffset.UtcNow,
            Author = "test",
            Message = message,
            Files = new Dictionary<string, PackageFile>(StringComparer.Ordinal)
            {
                ["PKGBUILD"] = new() { Content = pkgbuild, Size = pkgbuild.Length, Hash = "h" }
            }
        };
    }

    [Fact]
    public async Task DiffTab_RendersUnifiedDiffForChangedRevision()
    {
        // No ?from, so the base is rev-1, the revision immediately older than head.
        var response = await _client!.GetAsync(
            "/package/shelly-bin/diff?to=rev-2", TestContext.Current.CancellationToken);
        // Razor HTML-escapes the diff text ('+' becomes &#x2B;), which the browser decodes before
        // highlight.js reads textContent - decode here so the assertions read like the rendered diff.
        var body = WebUtility.HtmlDecode(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Contains("language-diff", body, StringComparison.Ordinal);
            // The header carries the real package path, not a temp name.
            Assert.Contains("--- a/PKGBUILD", body, StringComparison.Ordinal);
            Assert.Contains("+++ b/PKGBUILD", body, StringComparison.Ordinal);
            Assert.Contains("-pkgname=old", body, StringComparison.Ordinal);
            Assert.Contains("+pkgname=new", body, StringComparison.Ordinal);
            Assert.Contains("1 file changed", body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task DiffTab_ShowsNoContentChangesWhenTheRangeIsIdentical()
    {
        var response = await _client!.GetAsync(
            "/package/shelly-bin/diff?from=rev-2&to=rev-2", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("These are the same revision", body, StringComparison.Ordinal);
    }
}
