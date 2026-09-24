using Atoll.Api.Services.Catalog;
using Xunit;

namespace Atoll.Api.Tests.Catalog;

public class RelevanceQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespaceProducesNoTerms(string? raw)
    {
        Assert.True(RelevanceQueryParser.Parse(raw).IsEmpty);
    }

    [Fact]
    public void SplitsOnWhitespaceAndComma()
    {
        var query = RelevanceQueryParser.Parse("vim editor,gui");

        Assert.Equal(["vim", "editor", "gui"], Raw(query));
    }

    [Fact]
    public void TrimsSegmentsAndDropsEmptyOnes()
    {
        var query = RelevanceQueryParser.Parse("  vim ,, editor  ");

        Assert.Equal(["vim", "editor"], Raw(query));
    }

    [Fact]
    public void DeduplicatesOnPostingIgnoringCase()
    {
        var query = RelevanceQueryParser.Parse("Vim vim VIM");

        Assert.Equal(["Vim"], Raw(query));
    }

    [Fact]
    public void TermCarriesRawAndLowercasedPostingAndDenseOrdinal()
    {
        var terms = RelevanceQueryParser.Parse("Foo bar").Terms;

        Assert.Equal(2, terms.Length);
        Assert.Multiple(() =>
        {
            Assert.Equal("Foo", terms[0].Raw);
            Assert.Equal("foo", terms[0].Posting);
            Assert.Equal(0, terms[0].Ordinal);
            Assert.Equal(1, terms[1].Ordinal);
        });
    }

    [Theory]
    [InlineData("git")]
    [InlineData("qt")]
    [InlineData("1337")]
    [InlineData("café")]
    public void UnusableSegmentKeepsRawWithNullPosting(string raw)
    {
        var term = Assert.Single(RelevanceQueryParser.Parse(raw).Terms);

        Assert.Equal(raw, term.Raw);
        Assert.Null(term.Posting);
    }

    [Fact]
    public void QueryAtMaxLengthIsAccepted()
    {
        var query = RelevanceQueryParser.Parse(new string('a', RelevanceQueryParser.MaxQueryLength));

        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void QueryOverMaxLengthThrows()
    {
        var raw = new string('a', RelevanceQueryParser.MaxQueryLength + 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => RelevanceQueryParser.Parse(raw));
    }

    [Fact]
    public void EightTermsAreAccepted()
    {
        var query = RelevanceQueryParser.Parse("aaa bbb ccc ddd eee fff ggg hhh");

        Assert.Equal(RelevanceQueryParser.MaxTerms, query.Terms.Length);
    }

    [Fact]
    public void NineTermsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RelevanceQueryParser.Parse("aaa bbb ccc ddd eee fff ggg hhh iii"));
    }

    private static string[] Raw(RelevanceQuery query) => [.. query.Terms.Select(term => term.Raw)];
}
