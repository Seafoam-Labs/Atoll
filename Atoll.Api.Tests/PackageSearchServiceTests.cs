using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class PackageSearchServiceTests
{
    [Test]
    public async Task QueryByProvidesAndWordsMatchesExpectedPackages()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageSearchService(store);

        var byProvides = query.FindByProvides(["shelly"]);
        var byWords = query.FindByWords(["handheld", "portable"]);

        Assert.That(byProvides, Has.Length.EqualTo(1));
        Assert.That(byProvides[0].Name, Is.EqualTo("shelly-bin"));

        Assert.That(byWords.Count, Is.EqualTo(2));
        Assert.That(byWords[0].Name, Is.EqualTo("portable-pro"));
        Assert.That(byWords[1].Name, Is.EqualTo("portable-kit"));
    }

    [Test]
    public async Task QueryByNameIgnoresUnknownEntries()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageSearchService(store);

        var result = query.FindByNames(["portable-kit", "not-real"]);

        Assert.That(result, Has.Length.EqualTo(1));
        Assert.That(result[0].Name, Is.EqualTo("portable-kit"));
    }
}