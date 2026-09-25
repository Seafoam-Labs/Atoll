using System.Collections.Immutable;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Ui;

public sealed class PackageCatalogServiceTests : IAsyncLifetime
{
    private PackageIndexStore _store = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private IReadOnlyList<string> _seededNames = [];

    public ValueTask InitializeAsync()
    {
        _store = new PackageIndexStore();
        _store.Replace(TestData.LoadSampleIndexes());
        _securityRepository = new InMemoryPackageSecurityRepository();
        _seededNames = [];

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private PackageCatalogService CreateService(int snapshotTtlSeconds = 30)
    {
        return new PackageCatalogService(
            new PackageSearchEngine(_store),
            new SeededNamesPackageService(_seededNames),
            _securityRepository,
            TestHybridCache.New(),
            Options.Create(new AtollOptions
            {
                Caching = new CachingOptions { SnapshotTtlSeconds = snapshotTtlSeconds }
            }));
    }

    /// <summary>Serves a synthetic name corpus instead of the three-package sample index.</summary>
    private PackageCatalogService CreateService(ImmutableDictionary<string, AurPackageMetadata> names)
    {
        _store.Replace(SearchIndexData.Empty with { ByNames = names });
        return CreateService();
    }

    [Fact]
    public async Task EmptyQueryReturnsAllPackagesSortedByName()
    {
        var result = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-kit", "portable-pro", "shelly-bin"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(3, result.TotalMatches);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.TotalPages);
    }

    [Fact]
    public async Task NameModeMatchesNameOrDescriptionCaseInsensitive()
    {
        var service = CreateService();

        var byName = await service.SearchAsync("PORTABLE", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var byDescription = await service.SearchAsync("emulator", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-kit", "portable-pro"],
            byName.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(["portable-pro"],
            byDescription.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task WordsModeRequiresEveryTokenToMatch()
    {
        var service = CreateService();

        var single = await service.SearchAsync("helper", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Words, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var combined = await service.SearchAsync("handheld emulator", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Words, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            single.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(["portable-pro"],
            combined.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ProvidesModeMatchesProvidesValuesOnly()
    {
        var service = CreateService();

        var hit = await service.SearchAsync("shelly", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Provides, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var miss = await service.SearchAsync("kit", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Provides, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            hit.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Empty(miss.Rows);
    }

    [Fact]
    public async Task RelevanceModeRanksProvidesAheadOfNamePrefixes()
    {
        var result = await CreateService().SearchAsync("portable", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);

        // portable-pro matches a provides value exactly; portable-kit only starts with the term.
        Assert.Equal(["portable-pro", "portable-kit"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RelevanceModeBreaksWordPostingTiesByVotes()
    {
        var result = await CreateService().SearchAsync("handheld", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);

        // Both rows match on the keyword posting alone; votes decide, against name order.
        Assert.Equal(["portable-pro", "portable-kit"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task RelevanceModeMatchesNameTokensAndReturnsNothingForUnknownQueries()
    {
        var service = CreateService();

        var token = await service.SearchAsync("kit", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);
        var noHit = await service.SearchAsync("brwose", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-kit"],
            token.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Empty(noHit.Rows);
    }

    [Fact]
    public async Task ExplicitSortsOrderTheRankedMembershipWithoutWideningIt()
    {
        // VotesAsc reverses rank order (portable-pro first) while TotalMatches still counts the ranked
        // membership, not the whole catalog.
        var result = await CreateService().SearchAsync("portable", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.VotesAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-kit", "portable-pro"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(2, result.TotalMatches);
    }

    [Fact]
    public async Task RelevanceSortFallsBackToNameOrderWithoutARankedQuery()
    {
        var service = CreateService();

        var blank = await service.SearchAsync("", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);
        var legacy = await service.SearchAsync("portable", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(["portable-kit", "portable-pro", "shelly-bin"],
                blank.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(["portable-kit", "portable-pro"],
                legacy.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task RankedResultsPageAndClampLikeTheLegacyModes()
    {
        // Every generated name starts with the queried prefix, so the ranked membership is the corpus.
        var service = CreateService(Corpus(124));

        var page1 = await service.SearchAsync("pkg", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, ct: TestContext.Current.CancellationToken);
        var clamped = await service.SearchAsync("pkg", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Relevance, CatalogSort.Relevance, page: 99, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(124, page1.TotalMatches);
            Assert.Equal(3, page1.TotalPages);
            Assert.Equal(PackageCatalogService.PageSize, page1.Rows.Count);
            Assert.Equal("pkg-0000", page1.Rows[0].Package.Name);
            Assert.Equal(3, clamped.Page);
            Assert.Equal(24, clamped.Rows.Count);
            Assert.Equal("pkg-0123", clamped.Rows[^1].Package.Name);
        });
    }

    [Fact]
    public async Task SeededFilterNarrowsToSeededOrIndexOnlyRows()
    {
        _seededNames = ["shelly-bin"];
        var service = CreateService();

        var seeded = await service.SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var indexOnly = await service.SearchAsync(null, CatalogSeededFilter.IndexOnly, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            seeded.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.True(seeded.Rows.Single().IsSeeded);
        Assert.Equal(["portable-kit", "portable-pro"],
            indexOnly.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.True(indexOnly.Rows.All(row => !row.IsSeeded));
    }

    [Fact]
    public async Task SecurityFilterNarrowsToSeededPackagesWithMatchingHeadStatus()
    {
        _seededNames = ["shelly-bin"];
        await _securityRepository.MarkPendingAsync("shelly-bin", "rev-1", isHead: true, PkgBuildSecurityScanner.CurrentPolicyVersion, ct: TestContext.Current.CancellationToken);

        var pending = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Pending, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var verifiedBefore = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Verified, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            pending.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(SecurityStatus.Pending, pending.Rows.Single().Head!.Status);
        Assert.Empty(verifiedBefore.Rows);

        await _securityRepository.TryClaimPendingScanAsync("owner", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
        await _securityRepository.CompleteScanAsync("shelly-bin", "rev-1", "owner", new ScanResult(SecurityStatus.Verified, []), PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);

        var verifiedAfter = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Verified, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            verifiedAfter.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task TwoHeadDocumentsForOnePackageStillServeTheCatalog()
    {
        _seededNames = ["shelly-bin"];
        // Appending a revision marks the new head pending before demoting the previous one, so both
        // documents read as head for a window.
        await _securityRepository.MarkPendingAsync("shelly-bin", "rev-1", isHead: true, PkgBuildSecurityScanner.CurrentPolicyVersion, ct: TestContext.Current.CancellationToken);
        await _securityRepository.MarkPendingAsync("shelly-bin", "rev-2", isHead: true, PkgBuildSecurityScanner.CurrentPolicyVersion, ct: TestContext.Current.CancellationToken);

        var result = await CreateService().SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Pending, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        Assert.Equal(SecurityStatus.Pending, result.Rows.Single().Head!.Status);
    }

    [Fact]
    public async Task SeedingThroughPackageServiceMakesTheNextSearchSeeTheSeededRow()
    {
        // The catalog and the writer share one HybridCache, as they do as host singletons: the
        // write-path tag removal, not a UI-side invalidator, is what refreshes the snapshot.
        var cache = TestHybridCache.New();
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        var packageService = new PackageService(
            repo,
            options,
            security,
            new PkgBuildSecurityScanner(),
            new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance),
            cache,
            _store);
        var catalog = new PackageCatalogService(new PackageSearchEngine(_store), packageService, security, cache, options);

        var ct = TestContext.Current.CancellationToken;
        Assert.Empty((await SearchSeededAsync(catalog, ct)).Rows);
        Assert.Empty((await SearchSeededAsync(catalog, ct)).Rows);
        // The snapshot is built once per window, not once per search.
        Assert.Equal(1, repo.ListCalls);

        await packageService.SeedFilesAsync("shelly-bin", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly-bin\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly-bin\n"
        });

        var seeded = await SearchSeededAsync(catalog, ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(["shelly-bin"], seeded.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.True(seeded.Rows.Single().IsSeeded);
            Assert.Equal(2, repo.ListCalls);
        });
    }

    private static Task<CatalogResult> SearchSeededAsync(PackageCatalogService catalog, CancellationToken ct) =>
        catalog.SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: ct);

    private static Task<PackageIndexResponse> SortedPageAsync(IPackageService packages, CancellationToken ct) =>
        packages.GetIndexPageAsync(1, 10, PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc, ct);

    [Fact]
    public async Task HeadRescanLeavesTheSnapshotToExpireWithoutReListingTheRankedNames()
    {
        // One cache shared by the ranker's writer, the catalog, and the rescan queue, as in the
        // host: the snapshot carries only <c>catalog</c>, so a head rescan invalidates nothing;
        // the list badge heals through the snapshot's expiry and the ranker stays warm.
        var cache = TestHybridCache.New();
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var scanner = new PkgBuildSecurityScanner();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        var packageService = new PackageService(
            repo,
            options,
            security,
            scanner,
            new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance),
            cache,
            _store);
        var catalog = new PackageCatalogService(new PackageSearchEngine(_store), packageService, security, cache, options);
        var status = new PackageSecurityStatusService(repo, security, scanner);

        var ct = TestContext.Current.CancellationToken;
        await packageService.SeedFilesAsync("shelly-bin", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=shelly-bin\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly-bin\n"
        });
        await security.CompleteScanAsync("shelly-bin", SecurityStatus.Verified);

        // Warming lists the names twice: once for the ranker, once for the snapshot. Only a ranked
        // sort reaches the ranker; name ascending pages straight out of the repository.
        await SortedPageAsync(packageService, ct);
        var warm = await SearchSeededAsync(catalog, ct);
        Assert.Multiple(() =>
        {
            Assert.Equal(2, repo.ListCalls);
            Assert.Equal(SecurityStatus.Verified, warm.Rows.Single().Head!.Status);
        });

        await status.QueueRescanAsync("shelly-bin", ct: ct);

        var stale = await SearchSeededAsync(catalog, ct);
        var stillWarm = await SortedPageAsync(packageService, ct);
        Assert.Multiple(() =>
        {
            Assert.Equal(2, repo.ListCalls);
            Assert.Equal(SecurityStatus.Verified, stale.Rows.Single().Head!.Status);
            Assert.Equal(["shelly-bin"], stillWarm.Items.Select(item => item.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task PrewarmBuildsTheSnapshotAheadOfTheFirstSearch()
    {
        var packages = new SeededNamesPackageService(["shelly-bin"]);
        var catalog = new PackageCatalogService(
            new PackageSearchEngine(_store), packages, _securityRepository, TestHybridCache.New(), Options.Create(new AtollOptions()));

        var ct = TestContext.Current.CancellationToken;
        await catalog.PrewarmAsync(ct);
        await catalog.PrewarmAsync(ct);

        // Filled once and then reused, so a swap-time prewarm does not rescan the repository.
        Assert.Equal(1, packages.ListCalls);

        var searched = await SearchSeededAsync(catalog, ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, packages.ListCalls);
            Assert.Equal(["shelly-bin"], searched.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task PrewarmLeavesTheSnapshotToExpireSoAnUntaggedHeadPromotionStillHeals()
    {
        // Unlike the ranker's warm, this one only fills. A head promoted by refresh drops no tag, so
        // its badge heals through the snapshot's expiry; re-storing the value the way the ranker does
        // would keep it alive indefinitely.
        var packages = new SeededNamesPackageService(["shelly-bin"]);
        var catalog = new PackageCatalogService(
            new PackageSearchEngine(_store), packages, _securityRepository, TestHybridCache.New(),
            Options.Create(new AtollOptions
            {
                Caching = new CachingOptions { SnapshotTtlSeconds = 1 }
            }));

        var ct = TestContext.Current.CancellationToken;
        await catalog.PrewarmAsync(ct);
        await Task.Delay(1400, ct);
        await catalog.PrewarmAsync(ct);

        Assert.Equal(2, packages.ListCalls);
    }

    [Fact]
    public async Task VotesDescendingSortOrdersByVotes()
    {
        var result = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-pro", "shelly-bin", "portable-kit"],
            result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Theory]
    [InlineData(CatalogSort.NameAsc, "pkg-a,pkg-b,pkg-c,pkg-d")]
    [InlineData(CatalogSort.NameDesc, "pkg-d,pkg-c,pkg-b,pkg-a")]
    [InlineData(CatalogSort.VotesAsc, "pkg-a,pkg-c,pkg-d,pkg-b")]
    [InlineData(CatalogSort.VotesDesc, "pkg-b,pkg-d,pkg-c,pkg-a")]
    [InlineData(CatalogSort.PopularityAsc, "pkg-b,pkg-d,pkg-a,pkg-c")]
    [InlineData(CatalogSort.PopularityDesc, "pkg-c,pkg-a,pkg-d,pkg-b")]
    [InlineData(CatalogSort.LastModifiedAsc, "pkg-d,pkg-a,pkg-b,pkg-c")]
    [InlineData(CatalogSort.LastModifiedDesc, "pkg-c,pkg-b,pkg-a,pkg-d")]
    public async Task EverySortOrdersTheCorpusByItsColumn(CatalogSort sort, string expected)
    {
        // Each column is a distinct permutation of 1-4, so no two sorts may agree.
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a") with { NumVotes = 1, Popularity = 3, LastModified = 2 };
        names["pkg-b"] = CreateMetadata("pkg-b") with { NumVotes = 4, Popularity = 1, LastModified = 3 };
        names["pkg-c"] = CreateMetadata("pkg-c") with { NumVotes = 2, Popularity = 4, LastModified = 4 };
        names["pkg-d"] = CreateMetadata("pkg-d") with { NumVotes = 3, Popularity = 2, LastModified = 1 };

        var result = await CreateService(names.ToImmutable())
            .SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, sort, ct: TestContext.Current.CancellationToken);

        Assert.Equal(expected.Split(','), result.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    [Fact]
    public async Task NamePopularityAndLastModifiedSortsBreakTiesByName()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a") with { Popularity = 5, LastModified = 100 };
        names["pkg-b"] = CreateMetadata("pkg-b") with { Popularity = 5, LastModified = 300 };
        names["pkg-c"] = CreateMetadata("pkg-c") with { Popularity = 9, LastModified = 100 };

        var service = CreateService(names.ToImmutable());

        var popularity = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.PopularityDesc, ct: TestContext.Current.CancellationToken);
        var lastModified = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.LastModifiedDesc, ct: TestContext.Current.CancellationToken);
        var nameDesc = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameDesc, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // pkg-a and pkg-b tie on popularity, so the name tie-break decides.
            Assert.Equal(["pkg-c", "pkg-a", "pkg-b"],
                popularity.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(["pkg-b", "pkg-a", "pkg-c"],
                lastModified.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(["pkg-c", "pkg-b", "pkg-a"],
                nameDesc.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task RowFiltersNarrowTotalsBeforePagingAndClamping()
    {
        // Only 75 of the 124 rows are seeded, so totals and page counts must follow the filter.
        _seededNames = ExpectedNames(0, 75);

        var service = CreateService(Corpus(124));

        var ct = TestContext.Current.CancellationToken;
        var firstPage = await service.SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: ct);
        var clamped = await service.SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 99, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.Equal(75, firstPage.TotalMatches);
            Assert.Equal(2, firstPage.TotalPages);
            Assert.Equal(PackageCatalogService.PageSize, firstPage.Rows.Count);
            Assert.Equal(2, clamped.Page);
            Assert.Equal(25, clamped.Rows.Count);
            Assert.Equal("pkg-0074", clamped.Rows[^1].Package.Name);
        });
    }

    [Fact]
    public async Task NonNameSortsBreakTiesByNameForStablePaging()
    {
        // All packages share the same votes/popularity/mtime, so the name tie-break is the only
        // thing keeping page boundaries deterministic across the cached sorted view.
        var service = CreateService(Corpus(PackageCatalogService.PageSize + 1));

        var page1 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 1, ct: TestContext.Current.CancellationToken);
        var page2 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 2, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(ExpectedNames(0, PackageCatalogService.PageSize),
                page1.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize, 1),
                page2.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task ResultsArePaginatedInPageSizeChunk()
    {
        var service = CreateService(Corpus(PackageCatalogService.PageSize * 2 + 1));

        var page1 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 1, ct: TestContext.Current.CancellationToken);
        var page2 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 2, ct: TestContext.Current.CancellationToken);
        var page3 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 3, ct: TestContext.Current.CancellationToken);
        var page4 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 4, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(PackageCatalogService.PageSize * 2 + 1, page1.TotalMatches);
            Assert.Equal(3, page1.TotalPages);
            Assert.Equal(1, page1.Page);
            Assert.Equal(ExpectedNames(0, PackageCatalogService.PageSize),
                page1.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize, PackageCatalogService.PageSize),
                page2.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize * 2, 1),
                page3.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
            // Pages past the end clamp to the last page so stale deep links land on real content.
            Assert.Equal(3, page4.Page);
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize * 2, 1),
                page4.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task OutOfRangePagesAreClampedToFirstPage()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var service = CreateService(names.ToImmutable());

        var result = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 0, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Page);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task SearchesReflectIndexReplacement()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var service = CreateService(names.ToImmutable());

        var before = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["pkg-a"], before.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);

        names["pkg-b"] = CreateMetadata("pkg-b");
        _store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var after = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["pkg-a", "pkg-b"], after.Rows.Select(row => row.Package.Name), StringComparer.Ordinal);
    }

    private static string[] ExpectedNames(int start, int count)
    {
        return [.. Enumerable.Range(start, count).Select(i => $"pkg-{i:0000}")];
    }

    /// <summary>An otherwise-uniform <c>pkg-0000</c>... corpus of the given size.</summary>
    private static ImmutableDictionary<string, AurPackageMetadata> Corpus(int count)
    {
        return ExpectedNames(0, count).ToImmutableDictionary(name => name, CreateMetadata, StringComparer.Ordinal);
    }

    private static AurPackageMetadata CreateMetadata(string name)
    {
        return new AurPackageMetadata(
            Id: 0,
            Name: name,
            PackageBaseId: 0,
            PackageBase: name,
            Version: "1.0.0-1",
            Description: "",
            Url: null,
            NumVotes: 0,
            Popularity: 0,
            OutOfDate: null,
            Maintainer: null,
            Submitter: null,
            FirstSubmitted: 0,
            LastModified: 0,
            UrlPath: "",
            Depends: [],
            MakeDepends: [],
            OptDepends: [],
            Conflicts: [],
            Provides: [],
            License: [],
            Keywords: [],
            CoMaintainers: []);
    }
}
