using Atoll.Api.Services.Catalog.Indexing;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Indexing;

public class TokenCleaningTests
{
    [Fact]
    public void SplitAndClean_Separators_SplitIntoTokens()
    {
        Assert.Equal(["foo", "bar", "baz"], Clean("foo-bar.baz"));
    }

    [Fact]
    public void SplitAndClean_CamelCaseBoundaries_SplitIntoTokens()
    {
        Assert.Equal(["foo", "bar", "baz"], Clean("FooBarBaz"));
    }

    [Fact]
    public void SplitAndClean_TokensBelowLengthFloor_DropsUnlessAllowedShort()
    {
        Assert.Equal(["abc"], Clean("ab", "abc"));
        Assert.Equal(["i3"], Clean("i3"));
        Assert.Equal(["xz"], Clean("xz"));
    }

    [Fact]
    public void SplitAndClean_NonAscii_DropsToken()
    {
        Assert.Empty(Clean("café"));
    }

    [Fact]
    public void SplitAndClean_LeadingTwoDigits_DropsButKeepsSingleLeadingDigit()
    {
        Assert.Empty(Clean("30fps"));
        Assert.Equal(["3dfoo"], Clean("3dfoo"));
    }

    [Fact]
    public void SplitAndClean_PureNumeric_DropsToken()
    {
        Assert.Empty(Clean("1337"));
    }

    [Fact]
    public void SplitAndClean_ProseStopWords_DropsButKeepsNameTokens()
    {
        Assert.Multiple(() =>
        {
            Assert.Empty(Clean("the", "and", "with"));
            Assert.Equal(["git", "api"], Clean("git", "api"));
        });
    }

    [Fact]
    public void SplitAndClean_RepeatedTokensAndSplits_Deduplicates()
    {
        Assert.Equal(["foo"], Clean("foo", "foo"));
        Assert.Equal(["foo"], Clean("foo-foo"));
    }

    [Theory]
    [InlineData("foo", "foo")]
    [InlineData("Foo", "foo")]
    [InlineData("i3", "i3")]
    [InlineData("ab", null)]
    [InlineData("the", null)]
    [InlineData("1337", null)]
    [InlineData("30fps", null)]
    [InlineData("café", null)]
    public void NormalizePosting_Segment_AppliesTheIndexingFilters(string segment, string? expected)
    {
        Assert.Equal(expected, TokenCleaning.NormalizePosting(segment));
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Foo")]
    [InlineData("i3")]
    [InlineData("ab")]
    [InlineData("git")]
    [InlineData("the")]
    [InlineData("1337")]
    [InlineData("30fps")]
    [InlineData("café")]
    public void SplitAndClean_SinglePart_AgreesWithNormalizePosting(string segment)
    {
        var expected = TokenCleaning.NormalizePosting(segment);
        var cleaned = Clean(segment);

        if (expected is null) Assert.Empty(cleaned);
        else Assert.Equal([expected], cleaned);
    }

    private static string[] Clean(params string[] tokens) => [.. TokenCleaning.SplitAndClean(tokens)];
}
