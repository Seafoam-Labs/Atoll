using System.Collections.Immutable;
using System.Diagnostics;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Ui;

/// <summary>
/// Steady-state cost probe for <see cref="PackageCatalogService.SearchAsync"/> at a realistic
/// index scale (~85k AUR packages). The first SearchAsync call also populates
/// the 30s seeded/head snapshot; warmup iterations absorb it, so measured calls reflect what
/// every page load, sort toggle, or filter change costs - not the periodic repository read.
///
/// Log-only measurements: output goes to <see cref="ITestOutputHelper"/>; run the single test
/// and read the table from the runner's detailed test output. Assertions are scale/behaviour
/// sanity checks, not timing gates, so this is safe to keep in the normal test run.
/// </summary>
public class PackageCatalogServicePerfTests
{
    private const int PackageCount = 85_000;
    private const int SeededCount = 1_000;
    private const int WarmupIterations = 5;
    private const int Iterations = 20;

    private static readonly string[] Syllables =
        ["core", "lib", "gtk", "python", "kernel", "net", "data", "util", "graph", "media", "text", "crypto", "web", "ai", "shell", "tool"];

    private readonly PackageCatalogService _service;
    private readonly string _singleHitQuery = null!;
    private readonly ITestOutputHelper _output;

    public PackageCatalogServicePerfTests(ITestOutputHelper output)
    {
        _output = output;
        var rng = new Random(12345);
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        var seeded = new string[SeededCount];

        for (var i = 0; i < PackageCount; i++)
        {
            var name = $"{Syllables[rng.Next(Syllables.Length)]}-{Syllables[rng.Next(Syllables.Length)]}-{i:d5}";
            names[name] = new AurPackageMetadata(
                Id: i,
                Name: name,
                PackageBaseId: i,
                PackageBase: name,
                Version: $"{rng.Next(1, 10)}.{rng.Next(0, 20)}.{rng.Next(0, 10)}-{rng.Next(1, 5)}",
                Description: Description(rng),
                Url: null,
                NumVotes: rng.Next(0, 2_000),
                Popularity: rng.NextDouble() * 30,
                OutOfDate: null,
                Maintainer: "maintainer",
                Submitter: "submitter",
                FirstSubmitted: 1_700_000_000L,
                LastModified: 1_700_000_000L + rng.Next(0, 10_000_000),
                UrlPath: "/cgit/",
                Depends: ["glibc>=2.38"],
                MakeDepends: ["gcc"],
                OptDepends: [],
                Conflicts: [],
                Provides: [],
                License: ["MIT"],
                Keywords: [Syllables[rng.Next(Syllables.Length)]],
                CoMaintainers: []);

            if (i < SeededCount)
                seeded[i] = name;
            if (i == 0)
                _singleHitQuery = name;
        }

        var store = new PackageIndexStore();
        // The ranked scenario reads the derived relevance structures, so build through the builder
        // rather than hand-assembling a snapshot that would rank nothing.
        store.Replace(PackageIndexBuilder.BuildFromPackages(names.Values));

        _service = new PackageCatalogService(
            new PackageSearchEngine(store),
            new SeededNamesPackageService(seeded),
            new InMemoryPackageSecurityRepository(),
            TestHybridCache.New(),
            Options.Create(new AtollOptions()));
    }

    [Fact]
    public async Task SearchAsyncCostAtRealisticIndexScale()
    {
        _output.WriteLine(
            $"index={PackageCount:N0} packages, seeded={SeededCount:N0}, " +
            $"page size={PackageCatalogService.PageSize}, iterations={Iterations}, .NET {Environment.Version}");

        // Worst case and the actual default page view: empty query matches the whole index.
        await RunScenarioAsync("default page load (empty q, NameAsc)",
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, expectedTotal: PackageCount);

        // Steady-state pagination: the NameAsc sorted view is warm from the scenario above,
        // so this is what every page click costs once a sort has been materialized.
        await RunScenarioAsync("page navigation (empty q, NameAsc, page 7)",
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, expectedTotal: PackageCount, page: 7);

        await RunScenarioAsync("sort toggle (empty q, VotesDesc)",
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.VotesDesc, expectedTotal: PackageCount);

        // Only ~1k rows survive the seeded filter; the filter probes row state for every
        // candidate (all 85k), but rows are materialized only for the rendered page.
        await RunScenarioAsync("seeded filter (empty q, Seeded, VotesDesc)",
            null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.VotesDesc, expectedTotal: SeededCount);

        await RunScenarioAsync("broad query (q=lib-, NameAsc)",
            "lib-", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, expectedTotal: null);

        await RunScenarioAsync("narrow query (single hit, NameAsc)",
            _singleHitQuery, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, expectedTotal: 1);

        // The catalog's default for a query: one linear name scan plus candidate aggregation and
        // sorting, then the same page slice and projection as the legacy modes.
        await RunScenarioAsync("ranked query (q=lib-, Best match)",
            "lib-", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Relevance, CatalogSort.Relevance, expectedTotal: null);
    }

    private async Task RunScenarioAsync(
        string label,
        string? query,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        CatalogSearchMode mode,
        CatalogSort sort,
        int? expectedTotal,
        int page = 1)
    {
        CatalogResult? last = null;

        var measurement = await MeasureAsync(async () =>
            last = await _service.SearchAsync(query, seededFilter, securityFilter, mode, sort, page, TestContext.Current.CancellationToken));

        _output.WriteLine(
            $"{label,-44} matches={last!.TotalMatches,6}  rows={last.Rows.Count,3}  " +
            $"median={measurement.MedianMs,8:F2} ms  min={measurement.MinMs,7:F2} ms  " +
            $"alloc={measurement.AllocatedBytesPerCall / 1024.0,9:F0} KB/call");

        Assert.Multiple(() =>
        {
            if (expectedTotal is { } total)
                Assert.Equal(total, last.TotalMatches);

            Assert.Equal(Math.Min(last.TotalMatches, PackageCatalogService.PageSize), last.Rows.Count);
            Assert.Equal(
                (last.TotalMatches + PackageCatalogService.PageSize - 1) / PackageCatalogService.PageSize,
                last.TotalPages);
        });
    }

    private static async Task<Measurement> MeasureAsync(Func<Task<CatalogResult>> call)
    {
        for (var i = 0; i < WarmupIterations; i++)
            await call();

        var samples = new double[Iterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await call();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)Iterations;
        Array.Sort(samples);
        return new Measurement(samples[samples.Length / 2], samples[0], allocatedPerCall);
    }

    private static string Description(Random rng)
    {
        return $"{Syllables[rng.Next(Syllables.Length)]} {Syllables[rng.Next(Syllables.Length)]} utility " +
               $"for {Syllables[rng.Next(Syllables.Length)]} processing with {Syllables[rng.Next(Syllables.Length)]} support";
    }

    private sealed record Measurement(double MedianMs, double MinMs, double AllocatedBytesPerCall);
}
