using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageSearchEngineTests
{
    [Fact]
    public void HydrateKeepsCallerOrderAndSkipsUnknownNames()
    {
        var (engine, snapshot) = CreateEngine();

        var result = engine.Hydrate(snapshot, ["portable-pro", "not-real", "shelly-bin"]);

        Assert.Equal(["portable-pro", "shelly-bin"], Names(result), StringComparer.Ordinal);
    }

    [Fact]
    public void FindByNameIsExactOrdinal()
    {
        var (engine, snapshot) = CreateEngine();

        Assert.Equal("shelly-bin", engine.FindByName(snapshot, "shelly-bin")?.Name);
        Assert.Null(engine.FindByName(snapshot, "Shelly-Bin"));
        Assert.Null(engine.FindByName(snapshot, "not-real"));
    }

    [Fact]
    public void MatchProvidesUnionsPostingsAndDeduplicatesAcrossKeys()
    {
        var engine = new PackageSearchEngine(new PackageIndexStore());
        var snapshot = PackageIndexBuilder.BuildFromPackages(
        [
            TestData.Package("multi-provider", provides: ["alias-one", "alias-two"]),
            TestData.Package("explicit-provider", provides: ["alias-one"])
        ]);

        var matched = engine.MatchProvides(snapshot, Query("alias-one", "alias-two"));

        Assert.Equal(["explicit-provider", "multi-provider"], Sorted(matched), StringComparer.Ordinal);
        Assert.Empty(engine.MatchProvides(snapshot, Query("alias")));
    }

    [Fact]
    public void MatchWordsIsNullOnEmptyTermSetOrAnyMissingTerm()
    {
        var (engine, snapshot) = CreateEngine();

        Assert.Null(engine.MatchWords(snapshot, Query()));
        Assert.Null(engine.MatchWords(snapshot, Query("handheld", "missing")));

        var intersection = engine.MatchWords(snapshot, Query("handheld", "emulator"));

        Assert.NotNull(intersection);
        Assert.Equal(["portable-pro"], Sorted(intersection), StringComparer.Ordinal);
    }

    [Fact]
    public void AllEnumeratesTheNameDictionary()
    {
        var (engine, snapshot) = CreateEngine();

        Assert.Equal(snapshot.ByNames.Values, engine.All(snapshot));
    }

    [Fact]
    public void PrimitivesOnACapturedSnapshotAnswerThatGenerationAfterASwap()
    {
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var engine = new PackageSearchEngine(store);
        var captured = engine.Capture();

        store.Replace(TestData.IndexFromNames(["next-generation"]));

        Assert.Multiple(() =>
        {
            Assert.Equal("shelly-bin", engine.FindByName(captured, "shelly-bin")?.Name);
            Assert.Null(engine.FindByName(captured, "next-generation"));
            Assert.Equal("portable-kit", Assert.Single(engine.Hydrate(captured, ["portable-kit"])).Name);
            Assert.Equal(["next-generation"],
                Names(engine.All(engine.Capture())), StringComparer.Ordinal);
        });
    }

    private static (PackageSearchEngine Engine, SearchIndexData Snapshot) CreateEngine()
    {
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var engine = new PackageSearchEngine(store);
        return (engine, engine.Capture());
    }

    private static HashSet<string> Query(params string[] values) => new(values, StringComparer.Ordinal);

    private static string[] Names(IEnumerable<AurPackageMetadata> packages) =>
        [.. packages.Select(package => package.Name)];

    private static string[] Sorted(IEnumerable<string> names) => [.. names.Order(StringComparer.Ordinal)];
}
