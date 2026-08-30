using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class ShellSyntaxTests
{
    [TestCase("curl example # comment", "curl example ")]
    [TestCase("echo '# not a comment'", "echo '# not a comment'")]
    [TestCase("echo \"# not a comment\"", "echo \"# not a comment\"")]
    [TestCase("# whole line is a comment", "")]
    [TestCase("echo 'done' # trailing", "echo 'done' ")]
    [TestCase("no comment here", "no comment here")]
    [TestCase("", "")]
    public void StripComment_removes_unquoted_hash_and_everything_after(string line, string expected)
    {
        Assert.That(ShellSyntax.StripComment(line), Is.EqualTo(expected));
    }

    [Test]
    public void StripComment_does_not_treat_hash_in_single_quotes_as_comment()
    {
        Assert.That(ShellSyntax.StripComment("url='https://x/#fragment'"), Is.EqualTo("url='https://x/#fragment'"));
    }

    [Test]
    public void StripComment_treats_hash_after_closing_single_quote_as_comment()
    {
        Assert.That(ShellSyntax.StripComment("echo 'ok'# comment"), Is.EqualTo("echo 'ok'"));
    }

    [Test]
    public void NormalizeForMatching_rejoins_empty_single_quote_obfuscation()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("c''u''rl example").Text, Is.EqualTo("curl example"));
    }

    [Test]
    public void NormalizeForMatching_rejoins_empty_double_quote_obfuscation()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("s\"\"u\"\"do whoami").Text, Is.EqualTo("sudo whoami"));
    }

    [Test]
    public void NormalizeForMatching_strips_backslash_escapes_in_front_of_non_whitespace()
    {
        // \$ outside quotes is just $ to the shell, so the de-obfuscator drops the backslash.
        Assert.That(ShellSyntax.NormalizeForMatching("echo \\$HOME").Text, Is.EqualTo("echo $HOME"));
    }

    [Test]
    public void NormalizeForMatching_preserves_backslash_whitespace_escape()
    {
        // \<space> is a literal escaped space in shell - keep it intact so word boundaries survive.
        Assert.That(ShellSyntax.NormalizeForMatching("echo\\ cat").Text, Is.EqualTo("echo\\ cat"));
    }

    [Test]
    public void NormalizeForMatching_preserves_double_backslash()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("echo \\\\").Text, Is.EqualTo("echo \\\\"));
    }

    [Test]
    public void NormalizeForMatching_combines_quote_and_escape_obfuscation()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("c''u\\rl example").Text, Is.EqualTo("curl example"));
    }

    [Test]
    public void NormalizeForMatching_strips_intra_word_single_quotes()
    {
        // The shell removes quotes between word characters via quote removal: c'u'rl is curl.
        Assert.That(ShellSyntax.NormalizeForMatching("c'u'rl example").Text, Is.EqualTo("curl example"));
    }

    [Test]
    public void NormalizeForMatching_strips_intra_word_double_quotes()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("s\"u\"do whoami").Text, Is.EqualTo("sudo whoami"));
    }

    [Test]
    public void NormalizeForMatching_keeps_quotes_at_word_edges()
    {
        // Edge quotes make the whole word a quoted string (display/argument text), so they
        // must survive normalization: 'npm' is not an invocation of npm.
        Assert.That(ShellSyntax.NormalizeForMatching("echo 'npm' install").Text, Is.EqualTo("echo 'npm' install"));
        Assert.That(ShellSyntax.NormalizeForMatching("echo \"curl\" x").Text, Is.EqualTo("echo \"curl\" x"));
    }

    [Test]
    public void NormalizeForMatching_keeps_quote_between_word_and_non_word_character()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("echo done'!'").Text, Is.EqualTo("echo done'!'"));
    }

    [Test]
    public void NormalizeForMatching_combines_intra_word_and_adjacent_pair_stripping()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("cu'r''l example").Text, Is.EqualTo("curl example"));
    }

    [Test]
    public void NormalizeForMatching_source_indices_survive_intra_word_quote_stripping()
    {
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("c'u'rl");

        Assert.That(text, Is.EqualTo("curl"));
        Assert.That(sourceIndices, Is.EqualTo([0, 2, 4, 5]));
    }

    [TestCase("c'u'rl", true, Description = "intra-word quotes split the tool name")]
    [TestCase("'curl'", false, Description = "edge quotes make it a quoted string")]
    [TestCase("echo 'npm'", false, Description = "quoted argument is display text")]
    [TestCase("c''u''rl", true, Description = "adjacent-pair obfuscation still works")]
    public void MatchesUnquotedTool_after_normalization_detects_intra_word_obfuscation_only(string text, bool expected)
    {
        var normalized = ShellSyntax.NormalizeForMatching(text).Text;

        Assert.That(ShellSyntax.MatchesUnquotedTool(normalized, "curl") ||
                    ShellSyntax.MatchesUnquotedTool(normalized, "npm"), Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeForMatching_source_indices_map_surviving_characters_back_to_the_original()
    {
        // c''u''rl -> curl; each normalized character keeps its original position.
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("c''u''rl");

        Assert.That(text, Is.EqualTo("curl"));
        Assert.That(sourceIndices, Is.EqualTo([0, 3, 6, 7]));
    }

    [Test]
    public void NormalizeForMatching_source_indices_skip_dropped_escapes()
    {
        var (text, sourceIndices) = ShellSyntax.NormalizeForMatching("\\$(x)");

        Assert.That(text, Is.EqualTo("$(x)"));
        Assert.That(sourceIndices, Is.EqualTo([1, 2, 3, 4]));
    }

    [Test]
    public void MatchesUnquotedTool_ignores_display_text_but_detects_command_substitution()
    {
        Assert.That(ShellSyntax.MatchesUnquotedTool("echo 'sudo whoami'", "sudo"), Is.False);
        Assert.That(ShellSyntax.MatchesUnquotedTool("echo \"$(sudo whoami)\"", "sudo"), Is.True);
    }

    [TestCase("sudo whoami", true)]
    [TestCase("sudo", true)]
    [TestCase("echo sudo", true)]
    [TestCase("sudo; ls", false, Description = "tool followed by non-whitespace boundary is not matched")]
    public void MatchesUnquotedTool_recognises_tool_positioning(string text, bool expected)
    {
        Assert.That(ShellSyntax.MatchesUnquotedTool(text, "sudo"), Is.EqualTo(expected));
    }

    [TestCase("echo \"sudo is great\"", false, Description = "inside double quotes - display text")]
    [TestCase("echo 'sudo'", false, Description = "inside single quotes - display text")]
    [TestCase("echo \"$(sudo whoami)\"", true, Description = "inside command substitution in double quotes - executed")]
    [TestCase("$(sudo whoami)", true, Description = "bare command substitution - executed")]
    public void MatchesUnquotedTool_distinguishes_display_from_execution(string text, bool expected)
    {
        Assert.That(ShellSyntax.MatchesUnquotedTool(text, "sudo"), Is.EqualTo(expected));
    }

    [TestCase("pseudo", false)]
    [TestCase("mysudo", false)]
    [TestCase("sudopy", false)]
    [TestCase("echo pseudo sudoku", false, Description = "substring matches must not be flagged")]
    public void MatchesUnquotedTool_rejects_substring_occurrences(string text, bool expected)
    {
        Assert.That(ShellSyntax.MatchesUnquotedTool(text, "sudo"), Is.EqualTo(expected));
    }

    [Test]
    public void MatchesUnquotedTool_returns_true_when_at_least_one_occurrence_is_unquoted()
    {
        // First occurrence is display text inside quotes; second is a real invocation.
        Assert.That(ShellSyntax.MatchesUnquotedTool("echo 'sudo'; sudo whoami", "sudo"), Is.True);
    }

    [Test]
    public void MatchesUnquotedTool_returns_false_when_tool_does_not_appear()
    {
        Assert.That(ShellSyntax.MatchesUnquotedTool("echo hello", "sudo"), Is.False);
    }

    [Test]
    public void ComputeQuotePositions_tracks_single_quoted_regions()
    {
        var positions = ShellSyntax.ComputeQuotePositions("a='x' b");

        Assert.That(positions[0].Region, Is.EqualTo(ShellSyntax.QuoteRegion.Normal));
        Assert.That(positions[3].Region, Is.EqualTo(ShellSyntax.QuoteRegion.SingleQuoted), "content inside single quotes");
        Assert.That(positions[6].Region, Is.EqualTo(ShellSyntax.QuoteRegion.Normal), "after the closing quote");
    }

    [Test]
    public void ComputeQuotePositions_tracks_double_quoted_regions()
    {
        var positions = ShellSyntax.ComputeQuotePositions("echo \"a b\" x");

        Assert.That(positions[6].Region, Is.EqualTo(ShellSyntax.QuoteRegion.DoubleQuoted));
        Assert.That(positions[11].Region, Is.EqualTo(ShellSyntax.QuoteRegion.Normal), "after the closing quote");
    }

    [Test]
    public void ComputeQuotePositions_tracks_command_substitution()
    {
        var positions = ShellSyntax.ComputeQuotePositions("$(cmd) x");

        Assert.That(positions[0].Region, Is.EqualTo(ShellSyntax.QuoteRegion.Normal), "the '$' is read in the outer region");
        Assert.That(positions[2].Region, Is.EqualTo(ShellSyntax.QuoteRegion.CommandSubstitution));
        Assert.That(positions[7].Region, Is.EqualTo(ShellSyntax.QuoteRegion.Normal), "after the closing paren");
    }

    [Test]
    public void ComputeQuotePositions_command_substitution_inside_double_quotes_is_not_quoted_content()
    {
        // "$(x)" executes: the substitution body must not be classified as quoted text.
        var positions = ShellSyntax.ComputeQuotePositions("\"$(x)\"");

        Assert.That(positions[1].Region, Is.EqualTo(ShellSyntax.QuoteRegion.DoubleQuoted), "the '$' itself");
        Assert.That(positions[3].Region, Is.EqualTo(ShellSyntax.QuoteRegion.CommandSubstitution), "the body");
    }

    [Test]
    public void ComputeQuotePositions_marks_backslash_escaped_characters()
    {
        var positions = ShellSyntax.ComputeQuotePositions("\\$(x)");

        Assert.That(positions[1].Escaped, Is.True, "the escaped '$'");
        Assert.That(positions[2].Escaped, Is.False);
    }

    [Test]
    public void ComputeQuotePositions_backslash_is_not_an_escape_inside_single_quotes()
    {
        var positions = ShellSyntax.ComputeQuotePositions("'\\$'");

        Assert.That(positions[2].Escaped, Is.False);
        Assert.That(positions[2].Region, Is.EqualTo(ShellSyntax.QuoteRegion.SingleQuoted));
    }

    [Test]
    public void IsEntirelyInQuotes_detects_match_created_inside_quotes_by_escape_stripping()
    {
        // The normalized $( only exists because the load-bearing backslash was dropped;
        // it sits inside double quotes of the original line.
        const string original = "echo \"\\$(date)\"";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);
        var matchIndex = normalized.IndexOf("$(", StringComparison.Ordinal);

        Assert.That(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, matchIndex, 2), Is.True);
    }

    [Test]
    public void IsEntirelyInQuotes_rejects_unquoted_matches()
    {
        const string original = "s''u''d''o rm";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);

        Assert.That(normalized, Does.StartWith("sudo"));
        Assert.That(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, 0, 4), Is.False);
    }

    [Test]
    public void IsEntirelyInQuotes_rejects_command_substitution_body_under_double_quotes()
    {
        // Code inside $(...) executes even under double quotes, so it is not "quoted text".
        const string original = "echo \"$(c\\url x)\"";
        var positions = ShellSyntax.ComputeQuotePositions(original);
        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(original);
        var matchIndex = normalized.IndexOf("curl", StringComparison.Ordinal);

        Assert.That(ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, matchIndex, 4), Is.False);
    }

    [Test]
    public void FindUnquotedTool_returns_invocation_index()
    {
        Assert.That(ShellSyntax.FindUnquotedTool("echo x; sudo whoami", "sudo"), Is.EqualTo(8));
    }

    [Test]
    public void FindUnquotedTool_returns_minus_one_for_quoted_display_text()
    {
        Assert.That(ShellSyntax.FindUnquotedTool("echo 'sudo whoami'", "sudo"), Is.EqualTo(-1));
    }

    [Test]
    public void FindUnquotedTool_returns_minus_one_when_absent()
    {
        Assert.That(ShellSyntax.FindUnquotedTool("echo hello", "sudo"), Is.EqualTo(-1));
    }
}