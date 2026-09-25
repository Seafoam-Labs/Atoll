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
    public void TermCarriesRawAndPostingsAndDenseOrdinal()
    {
        var terms = RelevanceQueryParser.Parse("Foo bar").Terms;

        Assert.Equal(2, terms.Length);
        Assert.Multiple(() =>
        {
            Assert.Equal("Foo", terms[0].Raw);
            Assert.Equal(["foo"], terms[0].Postings);
            Assert.Equal(0, terms[0].Ordinal);
            Assert.Equal(1, terms[1].Ordinal);
        });
    }

    [Theory]
    [InlineData("vim")]
    [InlineData("i3")]
    [InlineData("3dfoo")]
    public void SinglePartSegmentKeepsTheOnePostingItHadBefore(string raw)
    {
        Assert.Equal([raw.ToLowerInvariant()], Postings(raw));
    }

    [Theory]
    [InlineData("git")]
    [InlineData("qt")]
    [InlineData("1337")]
    [InlineData("café")]
    public void UnusableSegmentKeepsRawWithNoPostings(string raw)
    {
        var term = Assert.Single(RelevanceQueryParser.Parse(raw).Terms);

        Assert.Multiple(() =>
        {
            Assert.Equal(raw, term.Raw);
            Assert.Empty(term.Postings);
        });
    }

    [Theory]
    [InlineData("vim-extra", "vim-extra", "vim", "extra")]
    [InlineData("linux-zen-headers", "linux-zen-headers", "linux", "zen", "headers")]
    [InlineData("portable+pro", "portable+pro", "portable", "pro")]
    [InlineData("NeovimNightly", "neovimnightly", "neovim", "nightly")]
    // "git", "xml" and "http" are stop-listed, so those parts drop out while the whole survives.
    [InlineData("neovim-git", "neovim-git", "neovim")]
    [InlineData("XmlHttpRequest", "xmlhttprequest", "request")]
    public void CompoundSegmentResolvesThroughItsPartsAsWellAsItsWholeForm(
        string raw, params string[] expected)
    {
        Assert.Equal(expected, Postings(raw));
    }

    [Fact]
    public void CompoundSegmentIsStillOneTerm()
    {
        var term = Assert.Single(RelevanceQueryParser.Parse("neovim-git").Terms);

        Assert.Equal(0, term.Ordinal);
    }

    [Fact]
    public void DuplicateCompoundSegmentsCollapseToOneTerm()
    {
        Assert.Equal(["neovim-git"], Raw(RelevanceQueryParser.Parse("neovim-git Neovim-Git")));
    }

    [Fact]
    public void SubPostingsTruncateAtThePerTermCap()
    {
        var postings = Postings("aaa-bbb-ccc-ddd-eee-fff-ggg");

        Assert.Multiple(() =>
        {
            Assert.Equal(RelevanceQueryParser.MaxPostingsPerTerm, postings.Length);
            Assert.Equal(["aaa-bbb-ccc-ddd-eee-fff-ggg", "aaa", "bbb", "ccc", "ddd", "eee"], postings);
        });
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

    private static string[] Postings(string raw) => Assert.Single(RelevanceQueryParser.Parse(raw).Terms).Postings;
}
