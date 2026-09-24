using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Indexing;

public class TokenCleaningTests
{
    [Fact]
    public void SplitsOnSeparators()
    {
        Assert.Equal(["foo", "bar", "baz"], Clean("foo-bar.baz"));
    }

    [Fact]
    public void SplitsCamelCaseBoundaries()
    {
        Assert.Equal(["foo", "bar", "baz"], Clean("FooBarBaz"));
    }

    [Fact]
    public void DropsTokensBelowLengthFloorUnlessAllowedShort()
    {
        Assert.Equal(["abc"], Clean("ab", "abc"));
        Assert.Equal(["i3"], Clean("i3"));
        Assert.Equal(["xz"], Clean("xz"));
    }

    [Fact]
    public void DropsNonAscii()
    {
        Assert.Empty(Clean("café"));
    }

    [Fact]
    public void DropsLeadingTwoDigitsButKeepsSingleLeadingDigit()
    {
        Assert.Empty(Clean("30fps"));
        Assert.Equal(["3dfoo"], Clean("3dfoo"));
    }

    [Fact]
    public void DropsPureNumeric()
    {
        Assert.Empty(Clean("1337"));
    }

    [Fact]
    public void DropsStopWords()
    {
        Assert.Empty(Clean("git", "the", "api"));
    }

    [Fact]
    public void DeduplicatesAcrossTokensAndSplits()
    {
        Assert.Equal(["foo"], Clean("foo", "foo"));
        Assert.Equal(["foo"], Clean("foo-foo"));
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("Foo", "foo")]
    [InlineData("i3", "i3")]
    [InlineData("ab", null)]
    [InlineData("git", null)]
    [InlineData("1337", null)]
    [InlineData("30fps", null)]
    [InlineData("café", null)]
    public void NormalizePostingAppliesTheIndexingFilters(string segment, string? expected)
    {
        Assert.Equal(expected, TokenCleaning.NormalizePosting(segment));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Foo")]
    [InlineData("i3")]
    [InlineData("ab")]
    [InlineData("git")]
    [InlineData("1337")]
    [InlineData("30fps")]
    [InlineData("café")]
    public void SplitAndCleanOfASinglePartAgreesWithNormalizePosting(string segment)
    {
        var expected = TokenCleaning.NormalizePosting(segment);
        var cleaned = Clean(segment);

        if (expected is null) Assert.Empty(cleaned);
        else Assert.Equal([expected], cleaned);
    }

    private static string[] Clean(params string[] tokens) => [.. TokenCleaning.SplitAndClean(tokens)];
}
