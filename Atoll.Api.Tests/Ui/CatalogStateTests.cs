using Atoll.Api.Services.Ui;
using Xunit;

namespace Atoll.Api.Tests.Ui;

public sealed class CatalogStateTests
{
    [Theory]
    [InlineData(null, null, null, CatalogSearchMode.Relevance, CatalogSort.NameAsc)]
    [InlineData("", "name", null, CatalogSearchMode.Name, CatalogSort.NameAsc)]
    [InlineData("x", null, null, CatalogSearchMode.Relevance, CatalogSort.Relevance)]
    [InlineData("x", "name", null, CatalogSearchMode.Name, CatalogSort.NameAsc)]
    [InlineData("x", null, "votes-desc", CatalogSearchMode.Relevance, CatalogSort.VotesDesc)]
    [InlineData("x", "relevance", "relevance", CatalogSearchMode.Relevance, CatalogSort.Relevance)]
    [InlineData("x", "words", "relevance", CatalogSearchMode.Words, CatalogSort.NameAsc)]
    [InlineData("", "relevance", null, CatalogSearchMode.Relevance, CatalogSort.NameAsc)]
    [InlineData("  ", null, null, CatalogSearchMode.Relevance, CatalogSort.NameAsc)]
    public void FromQueryResolvesTheDefaults(
        string? q, string? mode, string? sort, CatalogSearchMode expectedMode, CatalogSort expectedSort)
    {
        var state = CatalogState.FromQuery(q, null, mode, null, null, sort);

        Assert.Multiple(() =>
        {
            Assert.Equal(expectedMode, state.Mode);
            Assert.Equal(expectedSort, state.Sort);
        });
    }

    [Fact]
    public void BareUrlSerializesNoParameters()
    {
        var state = CatalogState.FromQuery(null, null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.Equal(CatalogState.Default, state);
            Assert.All(state.ToQueryParameters().Values, value => Assert.Null(value));
        });
    }

    [Fact]
    public void RankedQueryOmitsModeAndSortParameters()
    {
        var parameters = CatalogState.FromQuery("x", null, null, null, null, null).ToQueryParameters();

        Assert.Multiple(() =>
        {
            Assert.Equal("x", Assert.IsType<string>(parameters["q"]));
            Assert.Null(parameters["mode"]);
            Assert.Null(parameters["sort"]);
        });
    }

    [Fact]
    public void ExplicitLegacyModeSerializesWithAQueryPresent()
    {
        var state = CatalogState.FromQuery("x", null, "name", null, null, null);
        var parameters = state.ToQueryParameters();

        Assert.Multiple(() =>
        {
            Assert.Equal(CatalogSearchMode.Name, state.Mode);
            Assert.Equal("name", Assert.IsType<string>(parameters["mode"]));
            Assert.Null(parameters["sort"]);
        });
    }

    [Fact]
    public void ExplicitNameSortSerializesWithAQueryPresent()
    {
        var state = CatalogState.FromQuery("x", null, null, null, null, "name-asc");
        var parameters = state.ToQueryParameters();

        Assert.Multiple(() =>
        {
            Assert.Equal(CatalogSearchMode.Relevance, state.Mode);
            Assert.Null(parameters["mode"]);
            Assert.Equal("name-asc", Assert.IsType<string>(parameters["sort"]));
        });
    }

    [Fact]
    public void BlankQueryWithRelevanceModeSerializesBare()
    {
        var state = CatalogState.FromQuery("", null, "relevance", null, null, null);
        var parameters = state.ToQueryParameters();

        Assert.Multiple(() =>
        {
            Assert.Equal(CatalogSearchMode.Relevance, state.Mode);
            Assert.Equal(CatalogSort.NameAsc, state.Sort);
            Assert.All(parameters.Values, value => Assert.Null(value));
        });
    }

    [Fact]
    public void RelevanceSortNormalizesToNameOrderWhereItCannotApply()
    {
        var legacyMode = CatalogState.FromQuery("x", null, "words", null, null, "relevance");
        var blankQuery = CatalogState.FromQuery("", null, "relevance", null, null, "relevance");
        var ranked = CatalogState.FromQuery("x", null, null, null, null, "relevance");

        Assert.Multiple(() =>
        {
            Assert.Equal(CatalogSort.NameAsc, legacyMode.Sort);
            Assert.Null(legacyMode.ToQueryParameters()["sort"]);
            Assert.Equal(CatalogSort.NameAsc, blankQuery.Sort);
            Assert.Null(blankQuery.ToQueryParameters()["sort"]);
            Assert.Equal(CatalogSort.Relevance, ranked.Sort);
            Assert.Null(ranked.ToQueryParameters()["sort"]);
        });
    }

    [Fact]
    public void FiltersAndPageKeepTheirOwnDefaults()
    {
        var parameters = CatalogState.FromQuery("x", 3, "words", "seeded", "flagged", null).ToQueryParameters();

        Assert.Multiple(() =>
        {
            Assert.Equal("x", Assert.IsType<string>(parameters["q"]));
            Assert.Equal(3, Assert.IsType<int>(parameters["page"]));
            Assert.Equal("words", Assert.IsType<string>(parameters["mode"]));
            Assert.Equal("seeded", Assert.IsType<string>(parameters["seeded"]));
            Assert.Equal("flagged", Assert.IsType<string>(parameters["security"]));
            Assert.Null(parameters["sort"]);
        });
    }

    [Fact]
    public void PageZeroClampsToOneAndDropsOutOfTheUrl()
    {
        var parameters = CatalogState.FromQuery("x", 0, null, null, null, null).ToQueryParameters();

        Assert.Null(parameters["page"]);
    }
}