using Atoll.Api.Services.Catalog;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class PackageQueryTests
{
    [Theory]
    [InlineData("Name", By.Name)]
    [InlineData("Provides", By.Provides)]
    [InlineData("Words", By.Words)]
    [InlineData("name", By.Name)]
    [InlineData("PROVIDES", By.Provides)]
    [InlineData("words", By.Words)]
    public void ValidValueParsesSuccessfully(string input, By expected)
    {
        var parsed = ByQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.By);
    }

    [Fact]
    public void InvalidValueReturnsFalse()
    {
        var parsed = ByQuery.TryParse("Invalid", out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void NullReturnsFalse()
    {
        var parsed = ByQuery.TryParse(null, out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void EmptyStringReturnsFalse()
    {
        var parsed = ByQuery.TryParse(string.Empty, out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    [Fact]
    public void WhitespaceReturnsFalse()
    {
        var parsed = ByQuery.TryParse("   ", out var result);

        Assert.False(parsed);
        Assert.Equal(default(By), result.By);
    }

    // The binder comma-joins repeated ?by= values and Enum.TryParse ORs comma-separated names:
    // Name is 0, so "name,words" collapses to Words and "provides,words" is an undefined pair.
    [Theory]
    [InlineData("name,words", By.Words)]
    [InlineData("provides,words", (By)3)]
    public void CommaJoinedValuesParseAsFlagCombinations(string input, By expected)
    {
        var parsed = ByQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(expected, result.By);
    }

    [Fact]
    public void RelevanceIsExplicitlyFourLeavingThreeUndefined()
    {
        Assert.Equal(4, (int)By.Relevance);
        Assert.False(Enum.IsDefined(typeof(By), 3));
    }

    [Theory]
    [InlineData("Relevance")]
    [InlineData("relevance")]
    [InlineData("RELEVANCE")]
    public void RelevanceParsesInAnyCasing(string input)
    {
        var parsed = ByQuery.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(By.Relevance, result.By);
    }

    [Fact]
    public void NamesAreSplitByComma()
    {
        var parsed = SearchQuery.TryParse("shelly,portable,portable", out var result);

        Assert.True(parsed);
        Assert.Equal(3, result.Query.Length);
        Assert.Equal("shelly", result.Query[0]);
        Assert.Equal("portable", result.Query[1]);
        Assert.Equal("portable", result.Query[2]);
    }

    [Fact]
    public void RawSurvivesTheCommaSplit()
    {
        var parsed = SearchQuery.TryParse("vim editor,gui", out var result);

        Assert.True(parsed);
        Assert.Multiple(() =>
        {
            Assert.Equal("vim editor,gui", result.Raw);
            Assert.Equal(["vim editor", "gui"], result.Query, StringComparer.Ordinal);
        });
    }

    [Fact]
    public void SpacesAreNotSeparators()
    {
        var parsed = SearchQuery.TryParse("vim editor", out var result);

        Assert.True(parsed);
        Assert.Equal("vim editor", Assert.Single(result.Query));
    }

    [Fact]
    public void PartsAreTrimmedAndEmptySegmentsDropped()
    {
        var parsed = SearchQuery.TryParse(" shelly , ,portable,, ", out var result);

        Assert.True(parsed);
        Assert.Equal(["shelly", "portable"], result.Query, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceSourceProducesNoParts(string source)
    {
        var parsed = SearchQuery.TryParse(source, out var result);

        Assert.True(parsed);
        Assert.Empty(result.Query);
    }
}
