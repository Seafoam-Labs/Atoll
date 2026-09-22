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
    public void StripComment_removes_unquoted_hash_and_everything_after(string line, string expected)
    {
        Assert.Equal(expected, ShellSyntax.StripComment(line));
    }

    [Fact]
    public void StripComment_does_not_treat_hash_in_single_quotes_as_comment()
    {
        Assert.Equal("url='https://x/#fragment'", ShellSyntax.StripComment("url='https://x/#fragment'"));
    }

    [Fact]
    public void StripComment_treats_hash_after_closing_single_quote_as_comment()
    {
        Assert.Equal("echo 'ok'", ShellSyntax.StripComment("echo 'ok'# comment"));
    }

    [Fact]
    public void NormalizeForMatching_rejoins_empty_single_quote_obfuscation()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c''u''rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_rejoins_empty_double_quote_obfuscation()
    {
        Assert.Equal("sudo whoami", ShellSyntax.NormalizeForMatching("s\"\"u\"\"do whoami").Text);
    }

    [Fact]
    public void NormalizeForMatching_strips_backslash_escapes_in_front_of_non_whitespace()
    {
        // \$ outside quotes is just $ to the shell, so the de-obfuscator drops the backslash.
        Assert.Equal("echo $HOME", ShellSyntax.NormalizeForMatching("echo \\$HOME").Text);
    }

    [Fact]
    public void NormalizeForMatching_preserves_backslash_whitespace_escape()
    {
        // \<space> is a literal escaped space in shell - keep it intact so word boundaries survive.
        Assert.Equal("echo\\ cat", ShellSyntax.NormalizeForMatching("echo\\ cat").Text);
    }

    [Fact]
    public void NormalizeForMatching_preserves_double_backslash()
    {
        Assert.Equal("echo \\\\", ShellSyntax.NormalizeForMatching("echo \\\\").Text);
    }

    [Fact]
    public void NormalizeForMatching_combines_quote_and_escape_obfuscation()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c''u\\rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_strips_intra_word_single_quotes()
    {
        // The shell removes quotes between word characters via quote removal: c'u'rl is curl.
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("c'u'rl example").Text);
    }

    [Fact]
    public void NormalizeForMatching_strips_intra_word_double_quotes()
    {
        Assert.Equal("sudo whoami", ShellSyntax.NormalizeForMatching("s\"u\"do whoami").Text);
    }

    [Fact]
    public void NormalizeForMatching_keeps_quotes_at_word_edges()
    {
        // Edge quotes make the whole word a quoted string (display/argument text), so they
        // must survive normalization: 'npm' is not an invocation of npm.
        Assert.Equal("echo 'npm' install", ShellSyntax.NormalizeForMatching("echo 'npm' install").Text);
        Assert.Equal("echo \"curl\" x", ShellSyntax.NormalizeForMatching("echo \"curl\" x").Text);
    }

    [Fact]
    public void NormalizeForMatching_keeps_quote_between_word_and_non_word_character()
    {
        Assert.Equal("echo done'!'", ShellSyntax.NormalizeForMatching("echo done'!'").Text);
    }

    [Fact]
    public void NormalizeForMatching_combines_intra_word_and_adjacent_pair_stripping()
    {
        Assert.Equal("curl example", ShellSyntax.NormalizeForMatching("cu'r''l example").Text);
    }

    [Fact]
    public void NormalizeForMatching_source_indices_survive_intra_word_quote_stripping()
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
    public void MatchesUnquotedTool_after_normalization_detects_intra_word_obfuscation_only(string text, bool expected)
    {
        var normalized = ShellSyntax.NormalizeForMatching(text).Text;

        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(normalized, "curl") ||
                    ShellSyntax.MatchesUnquotedTool(normalized, "npm"));
    }

    [Fact]
    public void NormalizeForMatching_source_indices_map_surviving_characters_back_to_the_original()
    {
        // c''u''rl -> curl; each normalized character keeps its original position.
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("c''u''rl");

        Assert.Equal("curl", text);
        Assert.Equal([0, 3, 6, 7], sourceIndices);
    }

    [Fact]
    public void NormalizeForMatching_source_indices_skip_dropped_escapes()
    {
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("\\$(x)");

        Assert.Equal("$(x)", text);
        Assert.Equal([1, 2, 3, 4], sourceIndices);
    }

    [Fact]
    public void MatchesUnquotedTool_ignores_display_text_but_detects_command_substitution()
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
    public void MatchesUnquotedTool_recognises_tool_positioning(string text, bool expected)
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
    public void MatchesUnquotedTool_distinguishes_display_from_execution(string text, bool expected)
    {
        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(text, "sudo"));
    }

    [Theory]
    [InlineData("pseudo", false)]
    [InlineData("mysudo", false)]
    [InlineData("sudopy", false)]
    // substring matches must not be flagged
    [InlineData("echo pseudo sudoku", false)]
    public void MatchesUnquotedTool_rejects_substring_occurrences(string text, bool expected)
    {
        Assert.Equal(expected, ShellSyntax.MatchesUnquotedTool(text, "sudo"));
    }

    [Fact]
    public void MatchesUnquotedTool_returns_true_when_at_least_one_occurrence_is_unquoted()
    {
        // First occurrence is display text inside quotes; second is a real invocation.
        Assert.True(ShellSyntax.MatchesUnquotedTool("echo 'sudo'; sudo whoami", "sudo"));
    }

    [Fact]
    public void MatchesUnquotedTool_returns_false_when_tool_does_not_appear()
    {
        Assert.False(ShellSyntax.MatchesUnquotedTool("echo hello", "sudo"));
    }

    [Fact]
    public void ComputeQuotePositions_tracks_single_quoted_regions()
    {
        var positions = ShellSyntax.ComputeQuotePositions("a='x' b");

        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[0].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.SingleQuoted, positions[3].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[6].Region);
    }

    [Fact]
    public void ComputeQuotePositions_tracks_double_quoted_regions()
    {
        var positions = ShellSyntax.ComputeQuotePositions("echo \"a b\" x");

        Assert.Equal(ShellSyntax.QuoteRegion.DoubleQuoted, positions[6].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[11].Region);
    }

    [Fact]
    public void ComputeQuotePositions_tracks_command_substitution()
    {
        var positions = ShellSyntax.ComputeQuotePositions("$(cmd) x");

        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[0].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.CommandSubstitution, positions[2].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.Normal, positions[7].Region);
    }

    [Fact]
    public void ComputeQuotePositions_command_substitution_inside_double_quotes_is_not_quoted_content()
    {
        // "$(x)" executes: the substitution body must not be classified as quoted text.
        var positions = ShellSyntax.ComputeQuotePositions("\"$(x)\"");

        Assert.Equal(ShellSyntax.QuoteRegion.DoubleQuoted, positions[1].Region);
        Assert.Equal(ShellSyntax.QuoteRegion.CommandSubstitution, positions[3].Region);
    }

    [Fact]
    public void ComputeQuotePositions_marks_backslash_escaped_characters()
    {
        var positions = ShellSyntax.ComputeQuotePositions("\\$(x)");

        Assert.True(positions[1].Escaped, "the escaped '$'");
        Assert.False(positions[2].Escaped);
    }

    [Fact]
    public void ComputeQuotePositions_backslash_is_not_an_escape_inside_single_quotes()
    {
        var positions = ShellSyntax.ComputeQuotePositions("'\\$'");

        Assert.False(positions[2].Escaped);
        Assert.Equal(ShellSyntax.QuoteRegion.SingleQuoted, positions[2].Region);
    }

    [Fact]
    public void IsEntirelyInQuotes_detects_match_created_inside_quotes_by_escape_stripping()
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
    public void IsEntirelyInQuotes_rejects_unquoted_matches()
    {
        const string original = "s''u''d''o rm";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);

        Assert.StartsWith("sudo", normalized, StringComparison.Ordinal);
        Assert.False(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, 0, 4));
    }

    [Fact]
    public void IsEntirelyInQuotes_rejects_command_substitution_body_under_double_quotes()
    {
        // Code inside $(...) executes even under double quotes, so it is not "quoted text".
        const string original = "echo \"$(c\\url x)\"";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);
        var matchIndex = normalized.IndexOf("curl", StringComparison.Ordinal);

        Assert.False(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, matchIndex, 4));
    }

    [Fact]
    public void FindUnquotedTool_returns_invocation_index()
    {
        Assert.Equal(8, ShellSyntax.FindUnquotedTool("echo x; sudo whoami", "sudo"));
    }

    [Fact]
    public void FindUnquotedTool_returns_minus_one_for_quoted_display_text()
    {
        Assert.Equal(-1, ShellSyntax.FindUnquotedTool("echo 'sudo whoami'", "sudo"));
    }

    [Fact]
    public void FindUnquotedTool_returns_minus_one_when_absent()
    {
        Assert.Equal(-1, ShellSyntax.FindUnquotedTool("echo hello", "sudo"));
    }
}