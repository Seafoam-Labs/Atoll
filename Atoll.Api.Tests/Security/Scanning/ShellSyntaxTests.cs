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
        Assert.That(ShellSyntax.NormalizeForMatching("c''u''rl example"), Is.EqualTo("curl example"));
    }

    [Test]
    public void NormalizeForMatching_rejoins_empty_double_quote_obfuscation()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("s\"\"u\"\"do whoami"), Is.EqualTo("sudo whoami"));
    }

    [Test]
    public void NormalizeForMatching_strips_backslash_escapes_in_front_of_non_whitespace()
    {
        // \$ outside quotes is just $ to the shell, so the de-obfuscator drops the backslash.
        Assert.That(ShellSyntax.NormalizeForMatching("echo \\$HOME"), Is.EqualTo("echo $HOME"));
    }

    [Test]
    public void NormalizeForMatching_preserves_backslash_whitespace_escape()
    {
        // \<space> is a literal escaped space in shell - keep it intact so word boundaries survive.
        Assert.That(ShellSyntax.NormalizeForMatching("echo\\ cat"), Is.EqualTo("echo\\ cat"));
    }

    [Test]
    public void NormalizeForMatching_preserves_double_backslash()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("echo \\\\"), Is.EqualTo("echo \\\\"));
    }

    [Test]
    public void NormalizeForMatching_combines_quote_and_escape_obfuscation()
    {
        Assert.That(ShellSyntax.NormalizeForMatching("c''u\\rl example"), Is.EqualTo("curl example"));
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

    // ===== ContainsHiddenCharacter =====

    [Test]
    public void ContainsHiddenCharacter_detects_bidi_and_zero_width_controls()
    {
        Assert.That(ShellSyntax.ContainsHiddenCharacter("rm\u200B -rf /"), Is.True);
        Assert.That(ShellSyntax.ContainsHiddenCharacter("safe\u202Etext"), Is.True);
        Assert.That(ShellSyntax.ContainsHiddenCharacter("plain text"), Is.False);
    }

    [TestCase("zwsp\u200B", true, Description = "zero-width space")]
    [TestCase("zwnj\u200C", true, Description = "zero-width non-joiner")]
    [TestCase("zwj\u200D", true, Description = "zero-width joiner")]
    [TestCase("bom\uFEFF", true, Description = "byte-order mark / zero-width no-break space")]
    [TestCase("lre\u202A", true, Description = "left-to-right embedding")]
    [TestCase("rle\u202B", true, Description = "right-to-left embedding")]
    [TestCase("pdf\u202C", true, Description = "pop directional formatting")]
    [TestCase("rlo\u202E", true, Description = "right-to-left override")]
    [TestCase("lri\u2066", true, Description = "left-to-right isolate")]
    [TestCase("rli\u2067", true, Description = "right-to-left isolate")]
    [TestCase("fsi\u2068", true, Description = "first strong isolate")]
    [TestCase("pdi\u2069", true, Description = "pop directional isolate")]
    public void ContainsHiddenCharacter_flags_unicode_bidi_and_zero_width_controls(string text, bool expected)
    {
        Assert.That(ShellSyntax.ContainsHiddenCharacter(text), Is.EqualTo(expected));
    }

    [TestCase("nul\u0000", true, Description = "null byte")]
    [TestCase("bel\u0007", true, Description = "bell")]
    [TestCase("del\u007F", true, Description = "delete")]
    [TestCase("c1\u0080", true, Description = "C1 control 0x80")]
    [TestCase("c1\u009F", true, Description = "C1 control 0x9F")]
    public void ContainsHiddenCharacter_flags_control_characters(string text, bool expected)
    {
        Assert.That(ShellSyntax.ContainsHiddenCharacter(text), Is.EqualTo(expected));
    }

    [TestCase("tab\there", false, Description = "tab is allowed")]
    [TestCase("cr\rtext", false, Description = "carriage return is allowed")]
    [TestCase("plain ascii ~", false)]
    [TestCase("unicode emoji \uD83C\uDF89", false, Description = "surrogate pair (astral plane) is allowed")]
    [TestCase("nbsp\u00A0", false, Description = "non-breaking space (0xA0) is outside the C1 control range")]
    public void ContainsHiddenCharacter_allows_whitespace_and_normal_text(string text, bool expected)
    {
        Assert.That(ShellSyntax.ContainsHiddenCharacter(text), Is.EqualTo(expected));
    }

    [Test]
    public void ContainsHiddenCharacter_returns_false_for_empty_string()
    {
        Assert.That(ShellSyntax.ContainsHiddenCharacter(""), Is.False);
    }
}