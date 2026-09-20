using System.Collections.Immutable;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Ui;

public class PackageCatalogServiceTests : IAsyncLifetime
{
    private PackageIndexStore _store = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private IReadOnlyList<string> _seededNames = [];

    public async ValueTask InitializeAsync()
    {
        _store = new PackageIndexStore();
        _store.Replace(await TestData.LoadSampleIndexesAsync());
        _securityRepository = new InMemoryPackageSecurityRepository();
        _seededNames = [];
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private PackageCatalogService CreateService()
    {
        return new PackageCatalogService(
            _store,
            new SeededNamesPackageService(_seededNames),
            _securityRepository);
    }

    [Fact]
    public async Task EmptyQueryReturnsAllPackagesSortedByName()
    {
        var result = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-kit", "portable-pro", "shelly-bin"],
            result.Rows.Select(row => row.Package.Name));
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
            byName.Rows.Select(row => row.Package.Name));
        Assert.Equal(["portable-pro"],
            byDescription.Rows.Select(row => row.Package.Name));
    }

    [Fact]
    public async Task WordsModeRequiresEveryTokenToMatch()
    {
        var service = CreateService();

        var single = await service.SearchAsync("helper", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Words, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var combined = await service.SearchAsync("handheld emulator", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Words, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            single.Rows.Select(row => row.Package.Name));
        Assert.Equal(["portable-pro"],
            combined.Rows.Select(row => row.Package.Name));
    }

    [Fact]
    public async Task ProvidesModeMatchesProvidesValuesOnly()
    {
        var service = CreateService();

        var hit = await service.SearchAsync("shelly", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Provides, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var miss = await service.SearchAsync("kit", CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Provides, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            hit.Rows.Select(row => row.Package.Name));
        Assert.Empty(miss.Rows);
    }

    [Fact]
    public async Task SeededFilterNarrowsToSeededOrIndexOnlyRows()
    {
        _seededNames = ["shelly-bin"];
        var service = CreateService();

        var seeded = await service.SearchAsync(null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        var indexOnly = await service.SearchAsync(null, CatalogSeededFilter.IndexOnly, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            seeded.Rows.Select(row => row.Package.Name));
        Assert.True(seeded.Rows.Single().IsSeeded);
        Assert.Equal(["portable-kit", "portable-pro"],
            indexOnly.Rows.Select(row => row.Package.Name));
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
            pending.Rows.Select(row => row.Package.Name));
        Assert.Equal(SecurityStatus.Pending, pending.Rows.Single().Head!.Status);
        Assert.Empty(verifiedBefore.Rows);

        await _securityRepository.TryClaimPendingScanAsync("owner", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
        await _securityRepository.CompleteScanAsync("shelly-bin", "rev-1", "owner", new ScanResult(SecurityStatus.Verified, []), PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);

        var verifiedAfter = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Verified, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["shelly-bin"],
            verifiedAfter.Rows.Select(row => row.Package.Name));
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
            result.Rows.Select(row => row.Package.Name));
        Assert.Equal(SecurityStatus.Pending, result.Rows.Single().Head!.Status);
    }

    [Fact]
    public async Task VotesDescendingSortOrdersByVotes()
    {
        var result = await CreateService().SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, ct: TestContext.Current.CancellationToken);

        Assert.Equal(["portable-pro", "shelly-bin", "portable-kit"],
            result.Rows.Select(row => row.Package.Name));
    }

    [Fact]
    public async Task NonNameSortsBreakTiesByNameForStablePaging()
    {
        // All packages share the same votes/popularity/mtime, so the name tie-break is the only
        // thing keeping page boundaries deterministic across the cached sorted view.
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        for (var i = 0; i < PackageCatalogService.PageSize + 1; i++)
        {
            var name = $"pkg-{i:0000}";
            names[name] = CreateMetadata(name);
        }

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var page1 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 1, ct: TestContext.Current.CancellationToken);
        var page2 = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 2, ct: TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(ExpectedNames(0, PackageCatalogService.PageSize),
                page1.Rows.Select(row => row.Package.Name));
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize, 1),
                page2.Rows.Select(row => row.Package.Name));
        });
    }

    [Fact]
    public async Task ResultsArePaginatedInPageSizeChunk()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        for (var i = 0; i <= PackageCatalogService.PageSize * 2; i++)
        {
            var name = $"pkg-{i:0000}";
            names[name] = CreateMetadata(name);
        }

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

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
                page1.Rows.Select(row => row.Package.Name));
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize, PackageCatalogService.PageSize),
                page2.Rows.Select(row => row.Package.Name));
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize * 2, 1),
                page3.Rows.Select(row => row.Package.Name));
            // Pages past the end clamp to the last page so stale deep links land on real content.
            Assert.Equal(3, page4.Page);
            Assert.Equal(ExpectedNames(PackageCatalogService.PageSize * 2, 1),
                page4.Rows.Select(row => row.Package.Name));
        });
    }

    [Fact]
    public async Task OutOfRangePagesAreClampedToFirstPage()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var result = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, page: 0, ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Page);
        Assert.Single(result.Rows);
    }

    [Fact]
    public async Task SearchesReflectIndexReplacement()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var before = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["pkg-a"], before.Rows.Select(row => row.Package.Name));

        names["pkg-b"] = CreateMetadata("pkg-b");
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var after = await service.SearchAsync(null, CatalogSeededFilter.All, CatalogSecurityFilter.Any, CatalogSearchMode.Name, CatalogSort.NameAsc, ct: TestContext.Current.CancellationToken);
        Assert.Equal(["pkg-a", "pkg-b"], after.Rows.Select(row => row.Package.Name));
    }

    private static string[] ExpectedNames(int start, int count)
    {
        return [.. Enumerable.Range(start, count).Select(i => $"pkg-{i:0000}")];
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
