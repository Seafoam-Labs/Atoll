using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageSearchServiceTests
{
    [Fact]
    public async Task QueryByProvidesAndWordsMatchesExpectedPackages()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageSearchService(store);

        var byProvides = query.FindByProvides(["shelly"]);
        var byWords = query.FindByWords(["handheld", "portable"]);

        Assert.Single(byProvides);
        Assert.Equal("shelly-bin", byProvides[0].Name);

        Assert.Equal(2, byWords.Count());
        Assert.Equal("portable-pro", byWords[0].Name);
        Assert.Equal("portable-kit", byWords[1].Name);
    }

    [Fact]
    public async Task QueryByNameIgnoresUnknownEntries()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageSearchService(store);

        var result = query.FindByNames(["portable-kit", "not-real"]);

        Assert.Single(result);
        Assert.Equal("portable-kit", result[0].Name);
    }
}