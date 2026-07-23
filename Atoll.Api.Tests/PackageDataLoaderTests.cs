using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class PackageDataLoaderTests
{
    [Test]
    public async Task LoaderBuildsAllThreeIndexes()
    {
        var path = await TestData.WriteSamplePackagesAsync();

        var indexes = await PackageDataLoader.LoadAsync(path, CancellationToken.None);

        AssertIndexesMatchSample(indexes);
    }

    [Test]
    public async Task BuildFromPackagesProducesSameIndexesAsLoadAsync()
    {
        var path = await TestData.WriteSamplePackagesAsync();
        var fromFile = await PackageDataLoader.LoadAsync(path, CancellationToken.None);

        var packages = SamplePackages();
        var fromPackages = PackageDataLoader.BuildFromPackages(packages);

        Assert.Multiple(() =>
        {
            Assert.That(fromPackages.ByNames.Keys, Is.EquivalentTo(fromFile.ByNames.Keys));
            Assert.That(fromPackages.ByProvides.Keys, Is.EquivalentTo(fromFile.ByProvides.Keys));
            Assert.That(fromPackages.ByWords.Keys, Is.EquivalentTo(fromFile.ByWords.Keys));
            AssertIndexesMatchSample(fromPackages);
        });
    }

    [Test]
    public void BuildFromPackagesSkipsPackagesWithoutAName()
    {
        var packages = new List<AurPackageMetadata>
        {
            SamplePackage("valid-pkg", "A valid package", ["valid"]),
            SamplePackage("", "Has no name", [])
        };

        var indexes = PackageDataLoader.BuildFromPackages(packages);

        Assert.Multiple(() =>
        {
            Assert.That(indexes.ByNames.ContainsKey("valid-pkg"), Is.True);
            Assert.That(indexes.ByNames, Has.Count.EqualTo(1));
        });
    }

    private static void AssertIndexesMatchSample(SearchIndexData indexes)
    {
        Assert.Multiple(() =>
        {
            Assert.That(indexes.ByNames.ContainsKey("shelly-bin"), Is.True);
            Assert.That(indexes.ByProvides.ContainsKey("shelly"), Is.True);
            Assert.That(indexes.ByProvides.ContainsKey("portable-kit"), Is.True);
            Assert.That(indexes.ByWords.ContainsKey("handheld"), Is.True);
            Assert.That(indexes.ByWords.ContainsKey("portable"), Is.True);
            Assert.That(indexes.ByWords.ContainsKey("i3"), Is.True);
            Assert.That(indexes.ByWords.ContainsKey("1337"), Is.False);
        });
    }

    private static IEnumerable<AurPackageMetadata> SamplePackages()
    {
        yield return SamplePackage(
            "shelly-bin",
            "Shelly: A Modern Arch Package Manager (prebuilt binary)",
            ["shelly"],
            ["helper", "AUR"]);

        yield return SamplePackage(
            "portable-kit",
            "Handheld gaming toolkit 1337 i3",
            [],
            ["handheld"]);

        yield return SamplePackage(
            "portable-pro",
            "Handheld gaming emulator",
            ["portable"],
            ["emulator", "fast"]);
    }

    private static AurPackageMetadata SamplePackage(
        string name,
        string description,
        string[] provides,
        string[]? keywords = null)
    {
        return new AurPackageMetadata(
            Random.Shared.NextInt64(),
            name,
            1,
            name,
            "1.0-1",
            description,
            null,
            0,
            0.0,
            null,
            null,
            null,
            0,
            0,
            $"/cgit/{name}.git",
            [],
            [],
            [],
            [],
            provides,
            [],
            keywords ?? [],
            []);
    }
}