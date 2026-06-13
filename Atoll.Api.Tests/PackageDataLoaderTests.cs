using NUnit.Framework;

namespace Atoll.Api.Tests;

public class PackageDataLoaderTests
{
    [Test]
    public async Task LoaderBuildsAllThreeIndexes()
    {
        var path = await TestData.WriteSamplePackagesAsync();

        var indexes = await PackageDataLoader.LoadAsync(path, CancellationToken.None);

        Assert.That(indexes.ByNames.ContainsKey("shelly-bin"), Is.True);
        Assert.That(indexes.ByProvides.ContainsKey("shelly"), Is.True);
        Assert.That(indexes.ByProvides.ContainsKey("portable-kit"), Is.True);
        Assert.That(indexes.ByWords.ContainsKey("handheld"), Is.True);
        Assert.That(indexes.ByWords.ContainsKey("portable"), Is.True);
        Assert.That(indexes.ByWords.ContainsKey("i3"), Is.True);
        Assert.That(indexes.ByWords.ContainsKey("1337"), Is.False);
    }
}
