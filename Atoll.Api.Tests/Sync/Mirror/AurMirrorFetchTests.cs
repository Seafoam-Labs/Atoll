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
    public async Task FetchAsync_NoRefs_ReturnsEmptyResult()
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
    public async Task FetchAsync_AllRefsPresent_ReturnsAllSucceeded()
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
    public async Task FetchAsync_SingleMissingRef_IsolatesViaBisection()
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
    public async Task FetchAsync_MultipleMissingRefs_IsolatesEach()
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
    public async Task FetchAsync_SingleBadRef_ReportsFailedWithoutInfiniteLoop()
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
    public async Task FetchAsync_AllOtherRefsFail_PreservesSucceededRefs()
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