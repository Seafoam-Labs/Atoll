using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class HiddenCharactersTests
{
    [Fact]
    public void FindHiddenCharacters_SingleControlPerLine_ReturnsOneEach()
    {
        Assert.Single(HiddenCharacters.FindHiddenCharacters("rm\u200B -rf /"));
        Assert.Single(HiddenCharacters.FindHiddenCharacters("safe\u202Etext"));
        Assert.Empty(HiddenCharacters.FindHiddenCharacters("plain text"));
    }

    [Theory]
    // zero-width space
    [InlineData("zwsp\u200B", true)]
    // zero-width non-joiner
    [InlineData("zwnj\u200C", true)]
    // zero-width joiner
    [InlineData("zwj\u200D", true)]
    // byte-order mark / zero-width no-break space
    [InlineData("bom\uFEFF", true)]
    // left-to-right embedding
    [InlineData("lre\u202A", true)]
    // right-to-left embedding
    [InlineData("rle\u202B", true)]
    // pop directional formatting
    [InlineData("pdf\u202C", true)]
    // right-to-left override
    [InlineData("rlo\u202E", true)]
    // left-to-right isolate
    [InlineData("lri\u2066", true)]
    // right-to-left isolate
    [InlineData("rli\u2067", true)]
    // first strong isolate
    [InlineData("fsi\u2068", true)]
    // pop directional isolate
    [InlineData("pdi\u2069", true)]
    public void FindHiddenCharacters_KnownBidiAndZeroWidthCharacters_Flagged(string text, bool expected)
    {
        var found = HiddenCharacters.FindHiddenCharacters(text);
        if (expected)
        {
            Assert.NotEmpty(found);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Theory]
    // null byte
    [InlineData("nul\u0000", true)]
    // bell
    [InlineData("bel\u0007", true)]
    // delete
    [InlineData("del\u007F", true)]
    // C1 control 0x80
    [InlineData("c1\u0080", true)]
    // C1 control 0x9F
    [InlineData("c1\u009F", true)]
    public void FindHiddenCharacters_KnownControlCharacters_Flagged(string text, bool expected)
    {
        var found = HiddenCharacters.FindHiddenCharacters(text);
        if (expected)
        {
            Assert.NotEmpty(found);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Theory]
    // tab is allowed
    [InlineData("tab\there", false)]
    // carriage return is allowed
    [InlineData("cr\rtext", false)]
    [InlineData("plain ascii ~", false)]
    // surrogate pair (astral plane) is allowed
    [InlineData("unicode emoji \uD83C\uDF89", false)]
    // non-breaking space (0xA0) is outside the C1 control range
    [InlineData("nbsp\u00A0", false)]
    public void FindHiddenCharacters_WhitespaceAndNormalText_Allowed(string text, bool expected)
    {
        var found = HiddenCharacters.FindHiddenCharacters(text);
        if (expected)
        {
            Assert.NotEmpty(found);
        }
        else
        {
            Assert.Empty(found);
        }
    }

    [Theory]
    // complete CSI sequence is skipped
    [InlineData("green\u001b[32mtext")]
    // multi-parameter sequence
    [InlineData("\u001b[1;33;40m")]
    // several sequences on one line
    [InlineData("a\u001b[0mb\u001b[96mc")]
    public void FindHiddenCharacters_CompleteAnsiCsiSequences_Skipped(string text)
    {
        Assert.Empty(HiddenCharacters.FindHiddenCharacters(text));
    }

    [Theory]
    // OSC sequences are not CSI - the ESC is kept
    [InlineData("link \u001b]8;;http://x")]
    // unterminated sequence - the ESC is kept
    [InlineData("cut \u001b[32")]
    // ESC without '[' is kept
    [InlineData("bare \u001bx")]
    public void FindHiddenCharacters_NonCsiEscapes_Kept(string text)
    {
        Assert.NotEmpty(HiddenCharacters.FindHiddenCharacters(text));
    }

    [Fact]
    public void FindHiddenCharacters_EmptyLine_ReturnsEmpty()
    {
        Assert.Empty(HiddenCharacters.FindHiddenCharacters(""));
    }

    private static bool FirstHiddenCharacterIsBenign(string text)
    {
        var found = HiddenCharacters.FindHiddenCharacters(text);
        Assert.NotEmpty(found);
        return HiddenCharacters.IsBenignHiddenCharacter(text, found[0], ShellSyntax.ComputeQuotePositions(text));
    }

    [Theory]
    // zero-width chars are inert even unquoted
    [InlineData("rm\u200B -rf /")]
    // control byte inside quotes is display data
    [InlineData("echo 'x\u0016y'")]
    // C1 byte next to a Latin-1 char is mojibake
    [InlineData("mv {\u00d1\u0082.cfg,\u0442.cfg}")]
    public void IsBenignHiddenCharacter_InertContexts_Accepted(string text)
    {
        Assert.True(FirstHiddenCharacterIsBenign(text));
    }

    [Theory]
    // bidi overrides never qualify
    [InlineData("evil\u202Esh")]
    // control byte outside quotes can alter the parsed word
    [InlineData("echo x\u0016y")]
    // isolated C1 byte is not mojibake
    [InlineData("x\u0082y")]
    // bare ESC (not CSI) drives terminal escapes even in quotes
    [InlineData("echo \"\u001b]0;title\u0007\"")]
    public void IsBenignHiddenCharacter_GenuinelyHiddenContexts_Rejected(string text)
    {
        Assert.False(FirstHiddenCharacterIsBenign(text));
    }
}
