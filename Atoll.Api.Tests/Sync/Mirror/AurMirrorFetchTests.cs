using Atoll.Api.Services.Sync.Mirror;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atoll.Api.Tests.Sync.Mirror;

public class AurMirrorFetchTests
{
    private static FakeAurMirror CreateMirror(params string[] badRefs)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-mirror-test-{Guid.NewGuid():N}");
        return new FakeAurMirror(tempPath, badRefs);
    }

    [Fact]
    public async Task FetchAsync_empty_returns_empty_result()
    {
        var mirror = CreateMirror();

        var result = await mirror.FetchAsync([], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Empty(result.Succeeded);
            Assert.Empty(result.Failed);
            Assert.Empty(mirror.AttemptedBatches);
        });
    }

    [Fact]
    public async Task FetchAsync_all_refs_present_returns_all_succeeded()
    {
        var mirror = CreateMirror();

        var result = await mirror.FetchAsync(["alpha", "beta", "gamma"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "alpha", "beta", "gamma" }, result.Succeeded, strict: true);
            Assert.Empty(result.Failed);
            Assert.Single(mirror.AttemptedBatches);
        });
    }

    [Fact]
    public async Task FetchAsync_isolates_single_missing_ref_via_bisection()
    {
        var mirror = CreateMirror("charlie");

        var result = await mirror.FetchAsync(
            ["alpha", "beta", "charlie", "delta", "echo"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "alpha", "beta", "delta", "echo" }, result.Succeeded, strict: true);
            Assert.Equivalent(new[] { "charlie" }, result.Failed, strict: true);
        });
    }

    [Fact]
    public async Task FetchAsync_isolates_multiple_missing_refs()
    {
        var mirror = CreateMirror("beta", "delta");

        var result = await mirror.FetchAsync(
            ["alpha", "beta", "gamma", "delta", "echo"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "alpha", "gamma", "echo" }, result.Succeeded, strict: true);
            Assert.Equivalent(new[] { "beta", "delta" }, result.Failed, strict: true);
        });
    }

    [Fact]
    public async Task FetchAsync_single_bad_ref_reports_it_as_failed_without_infinite_loop()
    {
        var mirror = CreateMirror("only-bad");

        var result = await mirror.FetchAsync(["only-bad"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Empty(result.Succeeded);
            Assert.Equivalent(new[] { "only-bad" }, result.Failed, strict: true);
        });
    }

    [Fact]
    public async Task FetchAsync_preserves_succeeded_refs_when_all_others_fail()
    {
        var mirror = CreateMirror("bad1", "bad2", "bad3");

        var result = await mirror.FetchAsync(
            ["good1", "bad1", "good2", "bad2", "good3", "bad3"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "good1", "good2", "good3" }, result.Succeeded, strict: true);
            Assert.Equivalent(new[] { "bad1", "bad2", "bad3" }, result.Failed, strict: true);
        });
    }

    private sealed class FakeAurMirror(string cachePath, IEnumerable<string> badRefs)
        : AurMirror("https://mirror.example/aur", cachePath, NullLogger<AurMirror>.Instance)
    {
        private readonly HashSet<string> _badRefs = new(badRefs, StringComparer.Ordinal);

        public List<IReadOnlyList<string>> AttemptedBatches { get; } = [];

        protected override Task FetchBatchCoreAsync(IReadOnlyList<string> pkgBases, CancellationToken ct)
        {
            AttemptedBatches.Add(pkgBases);

            return pkgBases.Any(_badRefs.Contains)
                ? throw new InvalidOperationException("simulated atomic fetch failure")
                : Task.CompletedTask;
        }
    }
}