using System.Collections.Immutable;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
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
        Assert.That(result.IsTruncated, Is.False);
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
        await _securityRepository.MarkPendingAsync("shelly-bin", "rev-1", isHead: true);

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

        await _securityRepository.TryClaimPendingScanAsync("owner", TimeSpan.FromMinutes(1));
        await _securityRepository.CompleteScanAsync(
            "shelly-bin", "rev-1", "owner", new ScanResult(SecurityStatus.Verified, []));

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
    public async Task ResultsBeyondRenderCapAreTruncated()
    {
        var names = ImmutableDictionary.CreateBuilder<string, AurPackageMetadata>(StringComparer.Ordinal);
        for (var i = 0; i <= PackageCatalogService.RenderCap; i++)
        {
            var name = $"pkg-{i:0000}";
            names[name] = CreateMetadata(name);
        }

        var store = new PackageIndexStore();
        store.Replace(SearchIndexData.Empty with { ByNames = names.ToImmutable() });

        var service = new PackageCatalogService(
            store, new SeededNamesPackageService([]), _securityRepository);

        var result = await service.SearchAsync(
            null, CatalogSeededFilter.All, CatalogSecurityFilter.Any,
            CatalogSearchMode.Name, CatalogSort.NameAsc);

        Assert.That(result.TotalMatches, Is.EqualTo(PackageCatalogService.RenderCap + 1));
        Assert.That(result.Rows, Has.Count.EqualTo(PackageCatalogService.RenderCap));
        Assert.That(result.IsTruncated, Is.True);
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
