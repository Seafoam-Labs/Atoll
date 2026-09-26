using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class ShellSyntaxTests
{
    [Theory]
    [InlineData("curl example # comment", "curl example ")]
    [InlineData("echo '# not a comment'", "echo '# not a comment'")]
    [InlineData("echo \"# not a comment\"", "echo \"# not a comment\"")]
    [InlineData("# whole line is a comment", "")]
    [InlineData("echo 'done' # trailing", "echo 'done' ")]
    [InlineData("no comment here", "no comment here")]
    [InlineData("", "")]
    public void StripComment_UnquotedHash_RemovesHashAndTrailingText(string line, string expected)
    {
        Assert.Equal(expected, ShellSyntax.StripComment(line));
    }

    [Fact]
    public void StripComment_HashInsideSingleQuotes_IsPreserved()
    {
        Assert.Equal("url='https://x/#fragment'", ShellSyntax.StripComment("url='https://x/#fragment'"));
    }

    [Fact]
    public void StripComment_HashAfterClosingSingleQuote_StartsComment()
    {
        Assert.Equal("echo 'ok'", ShellSyntax.StripComment("echo 'ok'# comment"));
    }

    [Fact]
    public void NormalizeForMatching_EmptySingleQuoteObfuscation_RejoinsTheWord()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c''u''rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_EmptyDoubleQuoteObfuscation_RejoinsTheWord()
    {
        Assert.Equal("sudo whoami", ShellSyntax.NormalizeForMatching("s\"\"u\"\"do whoami").Text);
    }

    [Fact]
    public void NormalizeForMatching_BackslashBeforeNonWhitespace_DropsTheBackslash()
    {
        // \$ outside quotes is just $ to the shell, so the de-obfuscator drops the backslash.
        Assert.Equal("echo $HOME", ShellSyntax.NormalizeForMatching("echo \\$HOME").Text);
    }

    [Fact]
    public void NormalizeForMatching_BackslashBeforeWhitespace_KeepsTheEscape()
    {
        // \<space> is a literal escaped space in shell - keep it intact so word boundaries survive.
        Assert.Equal("echo\\ cat", ShellSyntax.NormalizeForMatching("echo\\ cat").Text);
    }

    [Fact]
    public void NormalizeForMatching_DoubleBackslash_IsPreserved()
    {
        Assert.Equal("echo \\\\", ShellSyntax.NormalizeForMatching("echo \\\\").Text);
    }

    [Fact]
    public void NormalizeForMatching_QuoteAndEscapeObfuscation_RejoinsTheWord()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c''u\\rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_IntraWordSingleQuotes_AreStripped()
    {
        // The shell removes quotes between word characters via quote removal: c'u'rl is curl.
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c'u'rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_IntraWordDoubleQuotes_AreStripped()
    {
        Assert.Equal("sudo whoami", ShellSyntax.NormalizeForMatching("s\"u\"do whoami").Text);
    }

    [Fact]
    public void NormalizeForMatching_QuotesAtWordEdges_AreKept()
    {
        // Edge quotes make the whole word a quoted string (display/argument text), so they
        // must survive normalization: 'npm' is not an invocation of npm.
        Assert.Equal("echo 'npm' install", ShellSyntax.NormalizeForMatching("echo 'npm' install").Text);
        Assert.Equal("echo \"curl\" x", ShellSyntax.NormalizeForMatching("echo \"curl\" x").Text);
    }

    [Fact]
    public void NormalizeForMatching_QuoteBetweenWordAndNonWordCharacter_IsKept()
    {
        Assert.Equal("echo done'!'", ShellSyntax.NormalizeForMatching("echo done'!'").Text);
    }

    [Fact]
    public void NormalizeForMatching_IntraWordAndAdjacentPairQuotes_AreStripped()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("cu'r''l example").Text);
    }

    [Fact]
    public void NormalizeForMatching_IntraWordQuoteStripping_KeepsSourceIndices()
    {
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("c'u'rl");

        Assert.Equal("curl", text);
        Assert.Equal([0, 2, 4, 5], sourceIndices);
    }

    [Theory]
    // intra-word quotes split the tool name
    [InlineData("c'u'rl", true)]
    // edge quotes make it a quoted string
    [InlineData("'curl'", false)]
    // quoted argument is display text
    [InlineData("echo 'npm'", false)]
    // adjacent-pair obfuscation still works
    [InlineData("c''u''rl", true)]
    public void MatchesUnquotedTool_AfterNormalization_DetectsOnlyIntraWordObfuscation(string text, bool expected)
    {
        var normalized = ShellSyntax.NormalizeForMatching(text).Text;

        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(normalized, "curl") ||
                    ShellSyntax.MatchesUnquotedTool(normalized, "npm"));
    }

    [Fact]
    public void NormalizeForMatching_SurvivingCharacters_MapBackToOriginalIndices()
    {
        // c''u''rl -> curl; each normalized character keeps its original position.
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("c''u''rl");

        Assert.Equal("curl", text);
        Assert.Equal([0, 3, 6, 7], sourceIndices);
    }

    [Fact]
    public void NormalizeForMatching_DroppedEscape_SkipsItsSourceIndex()
    {
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("\\$(x)");

        Assert.Equal("$(x)", text);
        Assert.Equal([1, 2, 3, 4], sourceIndices);
    }

    [Fact]
    public void MatchesUnquotedTool_DisplayTextIgnored_CommandSubstitutionDetected()
    {
        Assert.False(ShellSyntax.MatchesUnquotedTool("echo 'sudo whoami'", "sudo"));
        Assert.True(ShellSyntax.MatchesUnquotedTool("echo \"$(sudo whoami)\"", "sudo"));
    }

    [Theory]
    [InlineData("sudo whoami", true)]
    [InlineData("sudo", true)]
    [InlineData("echo sudo", true)]
    // tool followed by non-whitespace boundary is not matched
    [InlineData("sudo; ls", false)]
    public void MatchesUnquotedTool_ToolPositioning_RequiresWordBoundary(string text, bool expected)
    {
        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(text, "sudo"));
    }

    [Theory]
    // inside double quotes - display text
    [InlineData("echo \"sudo is great\"", false)]
    // inside single quotes - display text
    [InlineData("echo 'sudo'", false)]
    // inside command substitution in double quotes - executed
    [InlineData("echo \"$(sudo whoami)\"", true)]
    // bare command substitution - executed
    [InlineData("$(sudo whoami)", true)]
    public void MatchesUnquotedTool_DisplayVersusExecution_Distinguishes(string text, bool expected)
    {
        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(text, "sudo"));
    }

    [Theory]
    [InlineData("pseudo", false)]
    [InlineData("mysudo", false)]
    [InlineData("sudopy", false)]
    // substring matches must not be flagged
    [InlineData("echo pseudo sudoku", false)]
    public void MatchesUnquotedTool_SubstringOccurrences_AreRejected(string text, bool expected)
    {
        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(text, "sudo"));
    }

    [Fact]
    public void MatchesUnquotedTool_OneUnquotedOccurrence_ReturnsTrue()
    {
        // First occurrence is display text inside quotes; second is a real invocation.
        Assert.True(ShellSyntax.MatchesUnquotedTool("echo 'sudo'; sudo whoami", "sudo"));
    }

    [Fact]
    public void MatchesUnquotedTool_ToolAbsent_ReturnsFalse()
    {
        Assert.False(ShellSyntax.MatchesUnquotedTool("echo hello", "sudo"));
    }

    [Fact]
    public void ComputeQuotePositions_SingleQuotedCharacters_ClassifiedAsSingleQuoted()
    {
        var positions = ShellSyntax.ComputeQuotePositions("a='x' b");

        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[0].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.SingleQuoted, positions[3].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[6].Region);
    }

    [Fact]
    public void ComputeQuotePositions_DoubleQuotedCharacters_ClassifiedAsDoubleQuoted()
    {
        var positions = ShellSyntax.ComputeQuotePositions("echo \"a b\" x");

        Assert.Equal(ShellSyntax.QuoteRegion.DoubleQuoted, positions[6].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[11].Region);
    }

    [Fact]
    public void ComputeQuotePositions_CommandSubstitutionCharacters_ClassifiedAsCommandSubstitution()
    {
        var positions = ShellSyntax.ComputeQuotePositions("$(cmd) x");

        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[0].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.CommandSubstitution, positions[2].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[7].Region);
    }

    [Fact]
    public void ComputeQuotePositions_CommandSubstitutionInsideDoubleQuotes_IsNotQuotedContent()
    {
        // "$(x)" executes: the substitution body must not be classified as quoted text.
        var positions = ShellSyntax.ComputeQuotePositions("\"$(x)\"");

        Assert.Equal(ShellSyntax.QuoteRegion.DoubleQuoted, positions[1].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.CommandSubstitution, positions[3].Region);
    }

    [Fact]
    public void ComputeQuotePositions_BackslashEscapedCharacters_AreMarkedEscaped()
    {
        var positions = ShellSyntax.ComputeQuotePositions("\\$(x)");

        Assert.True(positions[1].Escaped, "the escaped '$'");
        Assert.False(positions[2].Escaped);
    }

    [Fact]
    public void ComputeQuotePositions_BackslashInsideSingleQuotes_IsNotAnEscape()
    {
        var positions = ShellSyntax.ComputeQuotePositions("'\\$'");

        Assert.False(positions[2].Escaped);
        Assert.Equal(ShellSyntax.QuoteRegion.SingleQuoted, positions[2].Region);
    }

    [Fact]
    public void IsEntirelyInQuotes_EscapeStrippedMatchInsideQuotes_ReturnsTrue()
    {
        // The normalized $( only exists because the load-bearing backslash was dropped;
        // it sits inside double quotes of the original line.
        const string original = "echo \"\\$(date)\"";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);
        var matchIndex = normalized.IndexOf("$(", StringComparison.Ordinal);

        Assert.True(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, matchIndex, 2));
    }

    [Fact]
    public void IsEntirelyInQuotes_UnquotedMatch_ReturnsFalse()
    {
        const string original = "s''u''d''o rm";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);

        Assert.StartsWith("sudo", normalized, StringComparison.Ordinal);
        Assert.False(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, 0, 4));
    }

    [Fact]
    public void IsEntirelyInQuotes_CommandSubstitutionBodyUnderDoubleQuotes_ReturnsFalse()
    {
        // Code inside $(...) executes even under double quotes, so it is not "quoted text".
        const string original = "echo \"$(c\\url x)\"";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);
        var matchIndex = normalized.IndexOf("curl", StringComparison.Ordinal);

        Assert.False(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, matchIndex, 4));
    }

    [Fact]
    public void FindUnquotedTool_Invocation_ReturnsIndex()
    {
        Assert.Equal(8, ShellSyntax.FindUnquotedTool("echo x; sudo whoami", "sudo"));
    }

    [Fact]
    public void FindUnquotedTool_QuotedDisplayText_ReturnsMinusOne()
    {
        Assert.Equal(-1, ShellSyntax.FindUnquotedTool("echo 'sudo whoami'", "sudo"));
    }

    [Fact]
    public void FindUnquotedTool_ToolAbsent_ReturnsMinusOne()
    {
        Assert.Equal(-1, ShellSyntax.FindUnquotedTool("echo hello", "sudo"));
    }
}