using Atoll.Api.Services.Packages.Seed;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Seed;

public class AurMirrorFetchTests
{
    private static FakeAurMirror CreateMirror(params string[] badRefs)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"atoll-mirror-test-{Guid.NewGuid():N}");
        return new FakeAurMirror(tempPath, badRefs);
    }

    [Test]
    public async Task FetchAsync_empty_returns_empty_result()
    {
        var mirror = CreateMirror();

        var result = await mirror.FetchAsync([], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.Empty);
            Assert.That(result.Failed, Is.Empty);
            Assert.That(mirror.AttemptedBatches, Is.Empty);
        });
    }

    [Test]
    public async Task FetchAsync_all_refs_present_returns_all_succeeded()
    {
        var mirror = CreateMirror();

        var result = await mirror.FetchAsync(["alpha", "beta", "gamma"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.EquivalentTo(["alpha", "beta", "gamma"]));
            Assert.That(result.Failed, Is.Empty);
            Assert.That(mirror.AttemptedBatches, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task FetchAsync_isolates_single_missing_ref_via_bisection()
    {
        // One ref out of five is unreachable: the whole batch fails, bisection narrows it down.
        var mirror = CreateMirror("charlie");

        var result = await mirror.FetchAsync(
            ["alpha", "beta", "charlie", "delta", "echo"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.EquivalentTo(["alpha", "beta", "delta", "echo"]));
            Assert.That(result.Failed, Is.EquivalentTo(["charlie"]));
        });
    }

    [Test]
    public async Task FetchAsync_isolates_multiple_missing_refs()
    {
        var mirror = CreateMirror("beta", "delta");

        var result = await mirror.FetchAsync(
            ["alpha", "beta", "gamma", "delta", "echo"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.EquivalentTo(["alpha", "gamma", "echo"]));
            Assert.That(result.Failed, Is.EquivalentTo(["beta", "delta"]));
        });
    }

    [Test]
    public async Task FetchAsync_single_bad_ref_reports_it_as_failed_without_infinite_loop()
    {
        var mirror = CreateMirror("only-bad");

        var result = await mirror.FetchAsync(["only-bad"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.Empty);
            Assert.That(result.Failed, Is.EquivalentTo(["only-bad"]));
        });
    }

    [Test]
    public async Task FetchAsync_preserves_succeeded_refs_when_all_others_fail()
    {
        var mirror = CreateMirror("bad1", "bad2", "bad3");

        var result = await mirror.FetchAsync(
            ["good1", "bad1", "good2", "bad2", "good3", "bad3"],
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.EquivalentTo(["good1", "good2", "good3"]));
            Assert.That(result.Failed, Is.EquivalentTo(["bad1", "bad2", "bad3"]));
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