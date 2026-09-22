using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageSearchServiceTests
{
    [Fact]
    public void QueryByProvidesAndWordsMatchesExpectedPackages()
    {
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var query = new PackageSearchService(store);

        var byProvides = query.FindByProvides(new HashSet<string>(["shelly"], StringComparer.Ordinal));
        var byWords = query.FindByWords(new HashSet<string>(["handheld", "portable"], StringComparer.Ordinal));

        Assert.Equal("shelly-bin", Assert.Single(byProvides).Name);

        Assert.Equal(2, byWords.Length);
        Assert.Equal("portable-pro", byWords[0].Name);
        Assert.Equal("portable-kit", byWords[1].Name);
    }

    [Fact]
    public void QueryByNameIgnoresUnknownEntries()
    {
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var query = new PackageSearchService(store);

        var result = query.FindByNames(new HashSet<string>(["portable-kit", "not-real"], StringComparer.Ordinal));

        Assert.Equal("portable-kit", Assert.Single(result).Name);
    }
}