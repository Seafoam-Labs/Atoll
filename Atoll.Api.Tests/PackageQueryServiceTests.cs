using NUnit.Framework;

namespace Atoll.Api.Tests;

public class PackageQueryServiceTests
{
    [Test]
    public async Task QueryByProvidesAndWordsMatchesExpectedPackages()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageQueryService(store);

        var byProvides = query.FindByProvides(["shelly"]);
        var byWords = query.FindByWords(["handheld", "portable"]);

        Assert.That(byProvides, Has.Count.EqualTo(1));
        Assert.That(byProvides[0].GetProperty("Name").GetString(), Is.EqualTo("shelly-bin"));

        Assert.That(byWords.Count, Is.EqualTo(2));
        Assert.That(byWords[0].GetProperty("Name").GetString(), Is.EqualTo("portable-pro"));
        Assert.That(byWords[1].GetProperty("Name").GetString(), Is.EqualTo("portable-kit"));
    }

    [Test]
    public async Task QueryByNameIgnoresUnknownEntries()
    {
        var store = new PackageIndexStore();
        store.Replace(await TestData.LoadSampleIndexesAsync());
        var query = new PackageQueryService(store);

        var result = query.FindByNames(["portable-kit", "not-real"]);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].GetProperty("Name").GetString(), Is.EqualTo("portable-kit"));
    }
}
