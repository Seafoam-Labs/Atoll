using System.Collections.Immutable;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Ui;

public class PackageCatalogServiceTests
{
    private PackageIndexStore _store = null!;
    private InMemoryPackageSecurityRepository _securityRepository = null!;
    private IReadOnlyList<string> _seededNames = [];

    [SetUp]
    public async Task SetUp()
    {
        _store = new PackageIndexStore();
        _store.Replace(await TestData.LoadSampleIndexesAsync());
        _securityRepository = new InMemoryPackageSecurityRepository();
        _seededNames = [];
    }

    private PackageCatalogService CreateService()
    {
        return new PackageCatalogService(
            _store,
            new SeededNamesPackageService(_seededNames),
            _securityRepository);
    }

    [Test]
    public async Task EmptyQueryReturnsAllPackagesSortedByName()
    {
        var result = await CreateService().SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(result.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-kit", "portable-pro", "shelly-bin"]));
        Assert.That(result.TotalMatches, Is.EqualTo(3));
        Assert.That(result.Page, Is.EqualTo(1));
        Assert.That(result.TotalPages, Is.EqualTo(1));
    }

    [Test]
    public async Task NameModeMatchesNameOrDescriptionCaseInsensitive()
    {
        var service = CreateService();

        var byName = await service.SearchAsync(
            "PORTABLE", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);
        var byDescription = await service.SearchAsync(
            "emulator", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(byName.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-kit", "portable-pro"]));
        Assert.That(byDescription.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-pro"]));
    }

    [Test]
    public async Task WordsModeRequiresEveryTokenToMatch()
    {
        var service = CreateService();

        var single = await service.SearchAsync(
            "helper", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Words, CatalogSort.NameAsc);
        var combined = await service.SearchAsync(
            "handheld emulator", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Words, CatalogSort.NameAsc);

        Assert.That(single.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["shelly-bin"]));
        Assert.That(combined.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-pro"]));
    }

    [Test]
    public async Task ProvidesModeMatchesProvidesValuesOnly()
    {
        var service = CreateService();

        var hit = await service.SearchAsync(
            "shelly", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Provides, CatalogSort.NameAsc);
        var miss = await service.SearchAsync(
            "kit", CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Provides, CatalogSort.NameAsc);

        Assert.That(hit.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["shelly-bin"]));
        Assert.That(miss.Rows, Is.Empty);
    }

    [Test]
    public async Task SeededFilterNarrowsToSeededOrIndexOnlyRows()
    {
        _seededNames = ["shelly-bin"];
        var service = CreateService();

        var seeded = await service.SearchAsync(
            null, CatalogSeededFilter.Seeded, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);
        var indexOnly = await service.SearchAsync(
            null, CatalogSeededFilter.IndexOnly, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(seeded.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["shelly-bin"]));
        Assert.That(seeded.Rows.Single().IsSeeded, Is.True);
        Assert.That(indexOnly.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-kit", "portable-pro"]));
        Assert.That(indexOnly.Rows.All(row => !row.IsSeeded), Is.True);
    }

    [Test]
    public async Task SecurityFilterNarrowsToSeededPackagesWithMatchingHeadStatus()
    {
        _seededNames = ["shelly-bin"];
        await _securityRepository.MarkPendingAsync("shelly-bin", "rev-1", isHead: true, PkgBuildSecurityScanner.CurrentPolicyVersion);

        var pending = await CreateService().SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Pending,
            CatalogSearchMode.Name, CatalogSort.NameAsc);
        var verifiedBefore = await CreateService().SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Verified,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(pending.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["shelly-bin"]));
        Assert.That(pending.Rows.Single().Head!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(verifiedBefore.Rows, Is.Empty);

        await _securityRepository.TryClaimPendingScanAsync("owner", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
        await _securityRepository.CompleteScanAsync(
            "shelly-bin", "rev-1", "owner", new ScanResult(SecurityStatus.Verified, []),
            PkgBuildSecurityScanner.CurrentPolicyVersion);

        var verifiedAfter = await CreateService().SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Verified,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(verifiedAfter.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["shelly-bin"]));
    }

    [Test]
    public async Task VotesDescendingSortOrdersByVotes()
    {
        var result = await CreateService().SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.VotesDesc);

        Assert.That(result.Rows.Select(row => row.Package.Name),
            Is.EqualTo(["portable-pro", "shelly-bin", "portable-kit"]));
    }

    [Test]
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

        var page1 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 1);
        var page2 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.VotesDesc, page: 2);

        Assert.Multiple(() =>
        {
            Assert.That(page1.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(0, PackageCatalogService.PageSize)));
            Assert.That(page2.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(PackageCatalogService.PageSize, 1)));
        });
    }

    [Test]
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

        var page1 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, page: 1);
        var page2 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, page: 2);
        var page3 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, page: 3);
        var page4 = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, page: 4);

        Assert.Multiple(() =>
        {
            Assert.That(page1.TotalMatches, Is.EqualTo(PackageCatalogService.PageSize * 2 + 1));
            Assert.That(page1.TotalPages, Is.EqualTo(3));
            Assert.That(page1.Page, Is.EqualTo(1));
            Assert.That(page1.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(0, PackageCatalogService.PageSize)));
            Assert.That(page2.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(PackageCatalogService.PageSize, PackageCatalogService.PageSize)));
            Assert.That(page3.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(PackageCatalogService.PageSize * 2, 1)));
            // Pages past the end clamp to the last page so stale deep links land on real content.
            Assert.That(page4.Page, Is.EqualTo(3));
            Assert.That(page4.Rows.Select(row => row.Package.Name),
                Is.EqualTo(ExpectedNames(PackageCatalogService.PageSize * 2, 1)));
        });
    }

    [Test]
    public async Task OutOfRangePagesAreClampedToFirstPage()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var result = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc, page: 0);

        Assert.That(result.Page, Is.EqualTo(1));
        Assert.That(result.Rows, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SearchesReflectIndexReplacement()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        names["pkg-a"] = CreateMetadata("pkg-a");

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var before = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);
        Assert.That(before.Rows.Select(row => row.Package.Name), Is.EqualTo(["pkg-a"]));

        names["pkg-b"] = CreateMetadata("pkg-b");
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var after = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);
        Assert.That(after.Rows.Select(row => row.Package.Name), Is.EqualTo(["pkg-a", "pkg-b"]));
    }

    private static string[] ExpectedNames(int start, int count)
    {
        return Enumerable.Range(start, count).Select(i => $"pkg-{i:0000}").ToArray();
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
