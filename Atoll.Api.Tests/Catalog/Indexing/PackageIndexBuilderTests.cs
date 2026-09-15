using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Indexing;

public class PackageIndexBuilderTests
{
    [Fact]
    public async Task LoaderBuildsAllThreeIndexes()
    {
        var path = await TestData.WriteSamplePackagesAsync();

        var indexes = await PackageIndexBuilder.LoadAsync(path, CancellationToken.None);

        AssertIndexesMatchSample(indexes);
    }

    [Fact]
    public async Task BuildFromPackagesProducesSameIndexesAsLoadAsync()
    {
        var path = await TestData.WriteSamplePackagesAsync();
        var fromFile = await PackageIndexBuilder.LoadAsync(path, CancellationToken.None);

        var packages = SamplePackages();
        var fromPackages = PackageIndexBuilder.BuildFromPackages(packages);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(fromFile.ByNames.Keys, fromPackages.ByNames.Keys, strict: true);
            Assert.Equivalent(fromFile.ByProvides.Keys, fromPackages.ByProvides.Keys, strict: true);
            Assert.Equivalent(fromFile.ByWords.Keys, fromPackages.ByWords.Keys, strict: true);
            AssertIndexesMatchSample(fromPackages);
        });
    }

    [Fact]
    public void BuildFromPackagesSkipsPackagesWithoutAName()
    {
        var packages = new List<AurPackageMetadata>
        {
            SamplePackage("valid-pkg", "A valid package", ["valid"]),
            SamplePackage("", "Has no name", [])
        };

        var indexes = PackageIndexBuilder.BuildFromPackages(packages);

        Assert.Multiple(() =>
        {
            Assert.True(indexes.ByNames.ContainsKey("valid-pkg"));
            Assert.Single(indexes.ByNames);
        });
    }

    private static void AssertIndexesMatchSample(SearchIndexData indexes)
    {
        Assert.Multiple(() =>
        {
            Assert.True(indexes.ByNames.ContainsKey("shelly-bin"));
            Assert.True(indexes.ByProvides.ContainsKey("shelly"));
            Assert.True(indexes.ByProvides.ContainsKey("portable-kit"));
            Assert.True(indexes.ByWords.ContainsKey("handheld"));
            Assert.True(indexes.ByWords.ContainsKey("portable"));
            Assert.True(indexes.ByWords.ContainsKey("i3"));
            Assert.False(indexes.ByWords.ContainsKey("1337"));
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