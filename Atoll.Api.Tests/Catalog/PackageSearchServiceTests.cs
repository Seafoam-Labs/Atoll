using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageSearchServiceTests
{
    [Fact]
    public void FindByProvidesAndWords_SampleIndex_MatchesExpectedPackages()
    {
        var query = CreateService();

        var byProvides = query.FindByProvides(new HashSet<string>(["shelly"], StringComparer.Ordinal));
        var byWords = query.FindByWords(new HashSet<string>(["handheld", "portable"], StringComparer.Ordinal));

        Assert.Equal("shelly-bin", Assert.Single(byProvides).Name);

        Assert.Equal(2, byWords.Length);
        Assert.Equal("portable-pro", byWords[0].Name);
        Assert.Equal("portable-kit", byWords[1].Name);
    }

    [Fact]
    public void FindByNames_UnknownName_IsIgnored()
    {
        var query = CreateService();

        var result = query.FindByNames(new HashSet<string>(["portable-kit", "not-real"], StringComparer.Ordinal));

        Assert.Equal("portable-kit", Assert.Single(result).Name);
    }

    [Fact]
    public void FindByNames_ExactOrdinal_RejectsNearMisses()
    {
        var query = CreateService();

        var exact = query.FindByNames(Query("shelly-bin"));
        var wrongCase = query.FindByNames(Query("Shelly-Bin", "SHELLY-BIN"));
        var nearMisses = query.FindByNames(Query("portable", "shelly", "portable-kit-extra"));

        Assert.Equal("shelly-bin", Assert.Single(exact).Name);
        Assert.Empty(wrongCase);
        Assert.Empty(nearMisses);
    }

    [Fact]
    public void FindByWords_MissingWordOrWrongCase_ReturnsEmpty()
    {
        var query = CreateService();

        var allWords = query.FindByWords(Query("handheld", "emulator"));
        var missingWord = query.FindByWords(Query("handheld", "missing"));
        var wrongCase = query.FindByWords(Query("Handheld"));

        Assert.Equal("portable-pro", Assert.Single(allWords).Name);
        Assert.Empty(missingWord);
        Assert.Empty(wrongCase);
    }

    [Fact]
    public void FindByWords_DefaultCap_CapsAtFiftyOrderedByVotes()
    {
        var query = CreateService(VoteRankedIndex(60));

        var results = query.FindByWords(Query("pkg"));

        Assert.Equal(50, results.Length);
        Assert.Equal("pkg-59", results[0].Name);
        Assert.Equal("pkg-10", results[^1].Name);
    }

    [Fact]
    public void FindByWords_ConfiguredCap_CapsAtTheConfiguredLimit()
    {
        var query = CreateService(VoteRankedIndex(60), maxRankedResults: 20);

        var results = query.FindByWords(Query("pkg"));

        Assert.Equal(20, results.Length);
        Assert.Equal("pkg-59", results[0].Name);
        Assert.Equal("pkg-40", results[^1].Name);
    }

    [Fact]
    public void FindByNamesAndProvides_RankedCapConfigured_StayUncapped()
    {
        // /v1/packages rows hydrate their remaining fields through by=name batches of up to 100
        // names, so the ranked cap must not be generalized to the other two modes.
        var names = Enumerable.Range(0, 60).Select(i => $"pkg-{i:00}").ToArray();
        var query = CreateService(TestData.IndexFromNames(names), maxRankedResults: 1);

        Assert.Equal(60, query.FindByNames(Query(names)).Length);
        Assert.Equal(60, query.FindByProvides(Query(names)).Length);
    }

    [Fact]
    public void FindByProvides_ExactKeysAndOverlap_Deduplicates()
    {
        var query = CreateService(PackageIndexBuilder.BuildFromPackages(
        [
            TestData.Package("multi-provider", provides: ["alias-one", "alias-two"]),
            TestData.Package("explicit-provider", provides: ["alias-one"])
        ]));

        var exact = query.FindByProvides(Query("alias-one"));
        // Both keys resolve the same package through different aliases, so it appears once.
        var deduplicated = query.FindByProvides(Query("alias-one", "alias-two"));
        var substring = query.FindByProvides(Query("alias"));

        Assert.Equal(["explicit-provider", "multi-provider"], SortedNames(exact), StringComparer.Ordinal);
        Assert.Equal(["explicit-provider", "multi-provider"], SortedNames(deduplicated), StringComparer.Ordinal);
        Assert.Empty(substring);
    }

    [Fact]
    public void FindByProvides_NoExplicitProvides_FallsBackToSelfName()
    {
        var query = CreateService(PackageIndexBuilder.BuildFromPackages(
        [
            TestData.Package("plain-pkg"),
            TestData.Package("explicit-provider", provides: ["alias-one"])
        ]));

        var selfNameFallback = query.FindByProvides(Query("plain-pkg"));
        // A package with explicit Provides is not indexed under its own name.
        var explicitSelfName = query.FindByProvides(Query("explicit-provider"));
        var unknown = query.FindByProvides(Query("not-real"));

        Assert.Equal("plain-pkg", Assert.Single(selfNameFallback).Name);
        Assert.Empty(explicitSelfName);
        Assert.Empty(unknown);
    }

    private static PackageSearchService CreateService(SearchIndexData? index = null, int maxRankedResults = 50)
    {
        var store = new PackageIndexStore();
        store.Replace(index ?? TestData.LoadSampleIndexes());

        return new PackageSearchService(
            new PackageSearchEngine(store),
            Options.Create(new AtollOptions { Search = new SearchOptions { MaxRankedResults = maxRankedResults } }));
    }

    /// <summary>Equal-tier names whose only ranking signal is votes, so a cap cuts a known suffix.</summary>
    private static SearchIndexData VoteRankedIndex(int count) =>
        PackageIndexBuilder.BuildFromPackages(
            [.. Enumerable.Range(0, count).Select(i => TestData.Package($"pkg-{i:00}", votes: i))]);

    private static HashSet<string> Query(params string[] values) => new(values, StringComparer.Ordinal);

    private static string[] SortedNames(IEnumerable<AurPackageMetadata> packages)
    {
        return [.. packages.Select(package => package.Name).Order(StringComparer.Ordinal)];
    }
}
