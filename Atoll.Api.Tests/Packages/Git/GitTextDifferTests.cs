using Atoll.Api.Services.Git;
using Xunit;

namespace Atoll.Api.Tests.Packages.Git;

/// <summary>
///     Pins the CLI contract the diff page depends on: chunk shape, relabeled headers, and the fact that the
///     temp tree does not survive the call - including when the call is cancelled.
/// </summary>
[Trait("Category", "RequiresGit")]
public sealed class GitTextDifferTests : IAsyncLifetime
{
    private static readonly GitTextDiffer Differ = new();

    public async ValueTask InitializeAsync()
    {
        var (exitCode, _) = await GitClient.TryExecuteAsync(["--version"], CancellationToken.None);
        Assert.SkipUnless(exitCode == 0, "git binary is required for these tests");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task RendersUnifiedDiffLabeledWithTheCallersPath()
    {
        var result = await DiffAsync(
            new TextDiffEntry("PKGBUILD", "line1\nline2\nline3\n", "line1\nline2 changed\nline3\n"),
            TestContext.Current.CancellationToken);

        var chunk = result["PKGBUILD"];
        Assert.Multiple(() =>
        {
            Assert.Contains("--- a/PKGBUILD", chunk, StringComparison.Ordinal);
            Assert.Contains("+++ b/PKGBUILD", chunk, StringComparison.Ordinal);
            Assert.Contains("@@", chunk, StringComparison.Ordinal);
            Assert.Contains("-line2", chunk, StringComparison.Ordinal);
            Assert.Contains("+line2 changed", chunk, StringComparison.Ordinal);
            // The label is ours, so no temp name leaks into the rendered diff.
            Assert.DoesNotContain("%", chunk, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task AddedAndRemovedFilesGetTheRightDevNullSide()
    {
        var result = await Differ.DiffAsync(
            [
                new TextDiffEntry("sub/added.txt", null, "brand new\n"),
                new TextDiffEntry("gone.txt", "gone\n", null)
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Multiple(() =>
        {
            // The existence side comes from git's mode line, not from guessing which header to rewrite first.
            Assert.Contains("new file mode", result["sub/added.txt"], StringComparison.Ordinal);
            Assert.Contains("--- /dev/null", result["sub/added.txt"], StringComparison.Ordinal);
            Assert.Contains("+++ b/sub/added.txt", result["sub/added.txt"], StringComparison.Ordinal);

            Assert.Contains("deleted file mode", result["gone.txt"], StringComparison.Ordinal);
            Assert.Contains("--- a/gone.txt", result["gone.txt"], StringComparison.Ordinal);
            Assert.Contains("+++ /dev/null", result["gone.txt"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EmptyOneSidedFilesNeverProduceABogusHunk()
    {
        var result = await Differ.DiffAsync(
            [
                new TextDiffEntry("sub/empty.txt", null, ""),
                new TextDiffEntry("sub/empty-gone.txt", "", null)
            ],
            TestContext.Current.CancellationToken);

        // Whether git reports an empty one-sided file at all is its business - the contract is that a chunk, if
        // there is one, carries the real path and never a fabricated hunk. The page renders a summary line for
        // the no-chunk case, which is why this asserts a shape rather than a count.
        foreach (var (path, chunk) in result)
        {
            Assert.Contains($"diff --git a/{path} b/{path}", chunk, StringComparison.Ordinal);
            Assert.DoesNotContain("@@", chunk, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SpaceBearingAndNestedPathsRoundTrip()
    {
        var result = await DiffAsync(
            new TextDiffEntry("dir with spaces/file name.txt", "old\n", "new\n"),
            TestContext.Current.CancellationToken);

        // The temp name is a hash, so it carries no spaces; the returned key is still the real path.
        var chunk = result["dir with spaces/file name.txt"];
        Assert.Multiple(() =>
        {
            Assert.Contains("--- a/dir with spaces/file name.txt", chunk, StringComparison.Ordinal);
            Assert.Contains("+++ b/dir with spaces/file name.txt", chunk, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PathSegmentsThatLookLikeTheSidePrefixesDoNotConfuseTheLabel()
    {
        var result = await DiffAsync(
            new TextDiffEntry("a/weird.txt", "old\n", "new\n"), TestContext.Current.CancellationToken);

        var chunk = result["a/weird.txt"];
        Assert.StartsWith("diff --git a/a/weird.txt b/a/weird.txt", chunk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalPairProducesNoChunk()
    {
        var result = await DiffAsync(
            new TextDiffEntry("PKGBUILD", "same\n", "same\n"), TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task RemovedLineThatLooksLikeAHeaderStaysBodyText()
    {
        var result = await DiffAsync(
            new TextDiffEntry("PKGBUILD", "keep\n-- not a header\n", "keep\n"),
            TestContext.Current.CancellationToken);

        var chunk = result["PKGBUILD"];
        Assert.Multiple(() =>
        {
            // A removed line reading "-- not a header" is body text starting with "--- ", so only the one
            // real header may be rewritten.
            Assert.Contains("--- not a header", chunk, StringComparison.Ordinal);
            Assert.Equal(
                1,
                chunk.Split('\n').Count(line => string.Equals(line, "--- a/PKGBUILD", StringComparison.Ordinal)));
        });
    }

    [Fact]
    public async Task MovedFileIsReportedAsARemovalPlusAnAddition()
    {
        var result = await Differ.DiffAsync(
            [
                new TextDiffEntry("old/name.txt", "same content\n", null),
                new TextDiffEntry("new/name.txt", null, "same content\n")
            ],
            TestContext.Current.CancellationToken);

        // --no-renames: identical content on both sides would otherwise fold into a single rename chunk this
        // page cannot label per file, so a move is reported as the removal plus the addition it is.
        Assert.Equal(2, result.Count);
        Assert.Multiple(() =>
        {
            Assert.Contains("deleted file mode", result["old/name.txt"], StringComparison.Ordinal);
            Assert.Contains("new file mode", result["new/name.txt"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TraversalAndRootedPathsNeverEscapeTheTempTree()
    {
        // Stored paths come from upstream tarballs, so neither a ".." segment nor a rooted path may decide
        // where the temp file lands: the temp name is a hash, and the real path only ever labels the output.
        var escaped = Path.Combine(Path.GetTempPath(), "atoll-differ-escape.txt");
        var rooted = Path.Combine(Path.GetTempPath(), "atoll-differ-rooted.txt");
        File.Delete(escaped);
        File.Delete(rooted);

        var result = await Differ.DiffAsync(
            [
                new TextDiffEntry("../../atoll-differ-escape.txt", "old\n", "new\n"),
                new TextDiffEntry(rooted, "old\n", "new\n")
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Multiple(() =>
        {
            Assert.False(File.Exists(escaped));
            Assert.False(File.Exists(rooted));
            Assert.Contains(
                "--- a/../../atoll-differ-escape.txt",
                result["../../atoll-differ-escape.txt"],
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task EmptyEntryListTouchesNeitherTheFilesystemNorGit()
    {
        var result = await Differ.DiffAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task MissingTrailingNewlineIsReportedAndSurvivesLabeling()
    {
        var result = await DiffAsync(
            new TextDiffEntry("no-newline.txt", "old", "new"), TestContext.Current.CancellationToken);

        var chunk = result["no-newline.txt"];
        Assert.Multiple(() =>
        {
            Assert.Contains("\\ No newline at end of file", chunk, StringComparison.Ordinal);
            Assert.Contains("--- a/no-newline.txt", chunk, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task TempTreeIsRemovedAfterASuccessfulDiff()
    {
        var before = TempEntryCount();
        await DiffAsync(new TextDiffEntry("PKGBUILD", "old\n", "new\n"), TestContext.Current.CancellationToken);

        Assert.Equal(before, TempEntryCount());
    }

    [Fact]
    public async Task CancellationPropagatesAndStillRemovesTheTempTree()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var before = TempEntryCount();

        // The write loop observes the token and throws, so the finally has to clean up a half-written tree.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Differ.DiffAsync([new TextDiffEntry("PKGBUILD", "old\n", "new\n")], cts.Token));

        Assert.Equal(before, TempEntryCount());
    }

    private static int TempEntryCount() => Directory
        .EnumerateDirectories(Path.GetTempPath())
        .Count(dir =>
        {
            // Only a GitTextDiffer root has an a/ and a b/ side, so unrelated temp dirs in a parallel
            // test host cannot be mistaken for a leak.
            try
            {
                return Directory.Exists(Path.Combine(dir, "a")) && Directory.Exists(Path.Combine(dir, "b"));
            }
            catch
            {
                return false;
            }
        });

    private static Task<IReadOnlyDictionary<string, string>> DiffAsync(
        TextDiffEntry entry,
        CancellationToken ct) => Differ.DiffAsync([entry], ct);
}
