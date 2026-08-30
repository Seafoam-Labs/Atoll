using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class HiddenCharactersTests
{
    [Test]
    public void FindHiddenCharacters_detects_bidi_and_zero_width_controls()
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters("rm\u200B -rf /"), Has.Count.EqualTo(1));
        Assert.That(HiddenCharacters.FindHiddenCharacters("safe\u202Etext"), Has.Count.EqualTo(1));
        Assert.That(HiddenCharacters.FindHiddenCharacters("plain text"), Is.Empty);
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
    public void FindHiddenCharacters_flags_unicode_bidi_and_zero_width_controls(string text, bool expected)
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(text), expected ? Is.Not.Empty : Is.Empty);
    }

    [TestCase("nul\u0000", true, Description = "null byte")]
    [TestCase("bel\u0007", true, Description = "bell")]
    [TestCase("del\u007F", true, Description = "delete")]
    [TestCase("c1\u0080", true, Description = "C1 control 0x80")]
    [TestCase("c1\u009F", true, Description = "C1 control 0x9F")]
    public void FindHiddenCharacters_flags_control_characters(string text, bool expected)
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(text), expected ? Is.Not.Empty : Is.Empty);
    }

    [TestCase("tab\there", false, Description = "tab is allowed")]
    [TestCase("cr\rtext", false, Description = "carriage return is allowed")]
    [TestCase("plain ascii ~", false)]
    [TestCase("unicode emoji \uD83C\uDF89", false, Description = "surrogate pair (astral plane) is allowed")]
    [TestCase("nbsp\u00A0", false, Description = "non-breaking space (0xA0) is outside the C1 control range")]
    public void FindHiddenCharacters_allows_whitespace_and_normal_text(string text, bool expected)
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(text), expected ? Is.Not.Empty : Is.Empty);
    }

    [TestCase("green\u001b[32mtext", Description = "complete CSI sequence is skipped")]
    [TestCase("\u001b[1;33;40m", Description = "multi-parameter sequence")]
    [TestCase("a\u001b[0mb\u001b[96mc", Description = "several sequences on one line")]
    public void FindHiddenCharacters_skips_complete_ansi_csi_sequences(string text)
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(text), Is.Empty);
    }

    [TestCase("link \u001b]8;;http://x", Description = "OSC sequences are not CSI - the ESC is kept")]
    [TestCase("cut \u001b[32", Description = "unterminated sequence - the ESC is kept")]
    [TestCase("bare \u001bx", Description = "ESC without '[' is kept")]
    public void FindHiddenCharacters_keeps_non_csi_escapes(string text)
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(text), Is.Not.Empty);
    }

    [Test]
    public void FindHiddenCharacters_returns_empty_for_empty_string()
    {
        Assert.That(HiddenCharacters.FindHiddenCharacters(""), Is.Empty);
    }

    private static bool FirstHiddenCharacterIsBenign(string text)
    {
        var found = HiddenCharacters.FindHiddenCharacters(text);
        Assert.That(found, Is.Not.Empty, $"expected at least one hidden character in: {text}");
        return HiddenCharacters.IsBenignHiddenCharacter(text, found[0], ShellSyntax.ComputeQuotePositions(text));
    }

    [TestCase("rm\u200B -rf /", Description = "zero-width chars are inert even unquoted")]
    [TestCase("echo 'x\u0016y'", Description = "control byte inside quotes is display data")]
    [TestCase("mv {\u00d1\u0082.cfg,\u0442.cfg}", Description = "C1 byte next to a Latin-1 char is mojibake")]
    public void IsBenignHiddenCharacter_accepts_inert_contexts(string text)
    {
        Assert.That(FirstHiddenCharacterIsBenign(text), Is.True);
    }

    [TestCase("evil\u202Esh", Description = "bidi overrides never qualify")]
    [TestCase("echo x\u0016y", Description = "control byte outside quotes can alter the parsed word")]
    [TestCase("x\u0082y", Description = "isolated C1 byte is not mojibake")]
    [TestCase("echo \"\u001b]0;title\u0007\"", Description = "bare ESC (not CSI) drives terminal escapes even in quotes")]
    public void IsBenignHiddenCharacter_rejects_genuinely_hidden_characters(string text)
    {
        Assert.That(FirstHiddenCharacterIsBenign(text), Is.False);
    }
}
