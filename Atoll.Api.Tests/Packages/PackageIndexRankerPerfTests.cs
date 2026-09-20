using System.Collections.Immutable;
using System.Diagnostics;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Packages;

/// <summary>
/// Cost probe for the sorted <c>/v1/packages</c> path at a realistic seeded-set scale (~85k names),
/// exercising <see cref="PackageIndexRanker"/> through <see cref="PackageService.GetIndexPageAsync"/>
/// with the in-memory repository. Reports a cold sorted request, a cold view build per sort on a warm
/// generation, and the steady-state warm page, because each has a different trigger. Log-only: sanity
/// assertions rather than timing gates, so it stays in the normal test run.
/// </summary>
public class PackageIndexRankerPerfTests
{
    private const int PackageCount = 85_000;
    private const int PageLimit = 50;
    private const int RebuildIterations = 5;
    private const int WarmupIterations = 3;
    private const int WarmIterations = 20;

    private static readonly string[] Syllables =
        ["core", "lib", "gtk", "python", "kernel", "net", "data", "util", "graph", "media", "text", "crypto", "web", "ai", "shell", "tool"];

    private readonly ITestOutputHelper _output;
    private readonly InMemoryPackageRepository _repo;
    private readonly PackageIndexStore _store;
    private readonly IOptions<AtollOptions> _options;
    private readonly InMemoryPackageSecurityRepository _security;
    private readonly PkgBuildSecurityScanner _scanner;

    public PackageIndexRankerPerfTests(ITestOutputHelper output)
    {
        _output = output;
        _repo = new InMemoryPackageRepository();
        _options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        _security = new InMemoryPackageSecurityRepository();
        _scanner = new PkgBuildSecurityScanner();

        // Every tenth seeded name is absent from the catalog, so both key paths (real catalog row and
        // default keys) are sorted at scale.
        var rng = new Random(12345);
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < PackageCount; i++)
        {
            var name = $"{Syllables[rng.Next(Syllables.Length)]}-{Syllables[rng.Next(Syllables.Length)]}-{i:d5}";
            if (i % 10 != 0)
            {
                names[name] = new AurPackageMetadata(
                    Id: i,
                    Name: name,
                    PackageBaseId: i,
                    PackageBase: name,
                    Version: $"{rng.Next(1, 10)}.{rng.Next(0, 20)}.{rng.Next(0, 10)}-{rng.Next(1, 5)}",
                    Description: "synthetic package",
                    Url: null,
                    NumVotes: rng.Next(0, 2_000),
                    Popularity: rng.NextDouble() * 30,
                    OutOfDate: null,
                    Maintainer: "maintainer",
                    Submitter: "submitter",
                    FirstSubmitted: 1_700_000_000L,
                    LastModified: 1_700_000_000L + rng.Next(0, 10_000_000),
                    UrlPath: "/cgit/",
                    Depends: [],
                    MakeDepends: [],
                    OptDepends: [],
                    Conflicts: [],
                    Provides: [],
                    License: [],
                    Keywords: [],
                    CoMaintainers: []);
            }

            _repo.InsertSeedAsync(
                new PackageDocument
                {
                    Id = name,
                    PackageName = name,
                    CreatedAt = now,
                    UpdatedAt = now,
                    HeadRevisionId = "rev-0",
                    Revisions = [new PackageRevisionDocument { RevisionId = "rev-0", CreatedAt = now, Author = "probe", Message = "seed" }]
                },
                new PackageRevisionContentDocument
                {
                    Id = PackageSchema.RevisionDocumentId(name, "rev-0"),
                    PackageName = name,
                    RevisionId = "rev-0",
                    CreatedAt = now,
                    Author = "probe",
                    Message = "seed"
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        _store = new PackageIndexStore();
        _store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        _output.WriteLine(
            $"seeded={PackageCount:N0}, catalog={names.Count:N0}, limit={PageLimit}, " +
            $"rebuild iterations={RebuildIterations}, warm iterations={WarmIterations}, .NET {Environment.Version}");
    }

    [Fact]
    public async Task SortedIndexPageCostAtRealisticSeededScale()
    {
        // 1. Cold sorted request: what a request pays after a mutation, TTL expiry, or restart.
        //    A fresh service per sample keeps the ranker cold (generation + view + page).
        var coldSamples = new double[RebuildIterations];
        var coldAllocated = 0L;
        for (var i = 0; i < RebuildIterations; i++)
        {
            var coldService = CreateService();
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var response = await coldService.GetIndexPageAsync(1, PageLimit, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
            stopwatch.Stop();
            coldAllocated += GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            coldSamples[i] = stopwatch.Elapsed.TotalMilliseconds;

            Assert.Equal(PackageCount, response.TotalItems);
            Assert.Equal(PageLimit, response.Items.Count);
        }

        Array.Sort(coldSamples);
        _output.WriteLine(
            $"cold sorted request (fresh generation + view, votes desc)  " +
            $"median={coldSamples[coldSamples.Length / 2],7:F1} ms  min={coldSamples[0],7:F1} ms  " +
            $"alloc={coldAllocated / (double)RebuildIterations / 1024,7:F0} KB/call");

        // 2. Cold view build per sort: one service whose generation is warmed with the cheapest sort,
        //    so each first request below measures exactly one view build plus one page fetch.
        var service = CreateService();
        var warmup = await service.GetIndexPageAsync(1, PageLimit, PackageIndexSortBy.Name, PackageIndexSortOrder.Desc, TestContext.Current.CancellationToken);
        Assert.Equal(PackageCount, warmup.TotalItems);

        foreach (var (sortBy, order) in new[]
                 {
                     (PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc),
                     (PackageIndexSortBy.Votes, PackageIndexSortOrder.Asc),
                     (PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc),
                     (PackageIndexSortBy.Popularity, PackageIndexSortOrder.Asc),
                     (PackageIndexSortBy.Version, PackageIndexSortOrder.Desc),
                     (PackageIndexSortBy.Version, PackageIndexSortOrder.Asc)
                 })
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var stopwatch = Stopwatch.StartNew();
            var response = await service.GetIndexPageAsync(1, PageLimit, sortBy, order, TestContext.Current.CancellationToken);
            stopwatch.Stop();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

            Assert.Equal(PackageCount, response.TotalItems);
            Assert.Equal(PageLimit, response.Items.Count);

            _output.WriteLine(
                $"cold view (warm generation) {sortBy,-10} {order,-4}  " +
                $"{stopwatch.Elapsed.TotalMilliseconds,7:F1} ms  alloc={allocated / 1024.0,7:F0} KB");
        }

        // 3. Warm page: cached view slice plus one name-filtered repository read, the steady state.
        var warm = await MeasureAsync(
            () => service.GetIndexPageAsync(1, PageLimit, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc));

        _output.WriteLine(
            $"warm page (votes desc)  median={warm.MedianMs,7:F2} ms  min={warm.MinMs,7:F2} ms  " +
            $"alloc={warm.AllocatedBytesPerCall / 1024,7:F0} KB/call");
    }

    private PackageService CreateService()
    {
        return new PackageService(
            _repo,
            _options,
            _security,
            _scanner,
            new GitRepositoryCache(_repo, _security, _options, NullLogger<GitRepositoryCache>.Instance),
            _store);
    }

    private static async Task<Measurement> MeasureAsync(Func<Task<PackageIndexResponse>> call)
    {
        for (var i = 0; i < WarmupIterations; i++)
            await call();

        var samples = new double[WarmIterations];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (var i = 0; i < WarmIterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await call();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        var allocatedPerCall = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / (double)WarmIterations;
        Array.Sort(samples);
        return new Measurement(samples[samples.Length / 2], samples[0], allocatedPerCall);
    }

    private sealed record Measurement(double MedianMs, double MinMs, double AllocatedBytesPerCall);
}