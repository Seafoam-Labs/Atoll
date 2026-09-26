using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class HomographScannerTests
{
    private static List<SecurityFinding> Scan(string content, string path = "PKGBUILD")
    {
        return [.. HomographScanner.Scan(content, path)];
    }

    private static SecurityFinding SingleFinding(string content)
    {
        return Assert.Single(Scan(content));
    }

    // ===== scope: fields and value extraction =====

    [Theory]
    [InlineData("pkgname=foo\n")]
    [InlineData("pkgver=1.0\n")]
    [InlineData("pkgdesc=A perfectly ASCII description\n")]
    // comment lines are ignored
    [InlineData("# pkgname=evil\u0430\n")]
    // only assignments at line start match
    [InlineData("echo pkgname=evil\u0430\n")]
    // field names match exactly, not as suffixes
    [InlineData("mypkgname=evil\u0430\n")]
    // space before '=' is not a valid assignment
    [InlineData("pkgname = evil\u0430\n")]
    public void Scan_NonFieldAndAsciiLines_NotFlagged(string content)
    {
        Assert.Empty(Scan(content));
    }

    [Theory]
    // pkgname scalar
    [InlineData("pkgname=ev\u0430il")]
    // pkgname array
    [InlineData("pkgname=('ev\u0430il')")]
    // depends array
    [InlineData("depends=('ev\u0430il')")]
    // makedepends array
    [InlineData("makedepends=('ev\u0430il')")]
    // url scalar
    [InlineData("url=\"https://g\u0456thub.com/x\"")]
    // source array
    [InlineData("source=(\"https://g\u0456thub.com/x.tar.gz\")")]
    public void Scan_EveryCheckedField_FlagsHomograph(string content)
    {
        var finding = Assert.Single(Scan(content));
        Assert.Equal("homograph", finding.RuleId);
    }

    [Fact]
    public void Scan_IndentedAssignmentInsidePackageFunction_Checked()
    {
        var findings = Scan("package() {\n  depends=('pacman' 'ev\u0430il')\n}\n");

        var finding = Assert.Single(findings);
        Assert.Equal("PKGBUILD", finding.File);
    }

    [Fact]
    public void Scan_NonAsciiInTrailingComment_NotFlagged()
    {
        var findings = Scan("source=(\"https://example.com/x.tar.gz\") # 中文说明，构建时需要网络\n");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_NonAsciiAfterClosingQuote_NotFlagged()
    {
        var findings = Scan("url=\"https://example.com\" # комментарий\n");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_QuotedAndUnquotedValue_SameFinding()
    {
        // The edge quotes themselves are ASCII; only the value content matters.
        var single = Scan("url='https://g\u0456thub.com/x'");
        var unquoted = Scan("url=https://g\u0456thub.com/x");

        var singleFinding = Assert.Single(single);
        var unquotedFinding = Assert.Single(unquoted);
        Assert.Equal(unquotedFinding.Message, singleFinding.Message);
    }

    [Fact]
    public void Scan_EachArrayElement_CheckedForHomograph()
    {
        var findings = Scan("depends=('ok' '\u0430bc' 'def\u0435')");

        var finding = Assert.Single(findings);
        Assert.Contains("[U+0430]bc", finding.Message, StringComparison.Ordinal);
        Assert.Contains("def[U+0435]", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ArrayElementWithSpaces_KeptAsSingleValue()
    {
        var findings = Scan("source=('local file with \u0430 spaces.tar.gz')");

        var finding = Assert.Single(findings);
        Assert.Contains("local file with [U+0430] spaces.tar.gz", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_HomographFinding_CarriesRuleSeveritySnippetAndFile()
    {
        var finding = SingleFinding("url=\"https://g\u0456thub.com/x\"");

        Assert.Equal("homograph", finding.RuleId);
        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("url=\"https://g\u0456thub.com/x\"", finding.Snippet);
        Assert.Equal("PKGBUILD", finding.File);
    }

    // ===== check 1: hidden / invisible characters =====

    [Theory]
    // zero-width space
    [InlineData("url=\"https://example.com/\u200Bx\"")]
    // bidi override
    [InlineData("url=\"https://example.com/\u202Ex\"")]
    // zero-width non-joiner
    [InlineData("depends=('a\u200Cb')")]
    public void Scan_ZeroWidthAndBidiCharacters_Flagged(string content)
    {
        var finding = SingleFinding(content);
        Assert.Contains("hidden or invisible character", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_CombiningMarkPrependedToUrlScheme_Flagged()
    {
        // The live corpus false negative: U+0670 (Arabic superscript alef) prepended to
        // the scheme in poweriso-gui. NFC normalization cannot compose it away.
        var finding = SingleFinding("url=\"\u0670http://www.poweriso.com/download.htm\"");

        Assert.Contains("U+0670", finding.Message, StringComparison.Ordinal);
        Assert.Contains("hidden or invisible character", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_HiddenCharacter_MessageNamesCodePoint()
    {
        var finding = SingleFinding("url=\"https://example.com/\u200Bx\"");

        Assert.Contains("U+200B", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_HiddenCharacterAlongsideMixedScript_ReportsHiddenFirst()
    {
        // The value also mixes Latin with Cyrillic, but the hidden check fires first.
        var finding = SingleFinding("url=\"https://\u200Bg\u0456thub.com\"");

        Assert.Contains("hidden or invisible character", finding.Message, StringComparison.Ordinal);
    }

    // ===== check 2: mixed scripts =====

    [Theory]
    // Cyrillic i lookalike in host
    [InlineData("url=\"https://g\u0456thub.com/x\"", "Cyrillic")]
    // Greek iota lookalike in host
    [InlineData("url=\"https://g\u03B9thub.com/x\"", "Greek")]
    // Armenian letter in dependency
    [InlineData("depends=('a\u0562c')", "Armenian")]
    public void Scan_LatinMixedWithLookalikeScript_Flagged(string content, string scriptName)
    {
        var finding = SingleFinding(content);

        Assert.Contains("mixes Latin with", finding.Message, StringComparison.Ordinal);
        Assert.Contains(scriptName, finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // CJK and Hangul cannot spoof ASCII
    [InlineData("source=(\"https://开发자양반.info/x.tar.gz\")")]
    // pure CJK IDN without Latin
    [InlineData("source=(\"https://例え.测试/ファイル\")")]
    // pure Cyrillic without Latin is not mixed
    [InlineData("pkgname='\u0430\u0431\u0432'")]
    public void Scan_CjkHangulAndSingleScriptValues_NotFlagged(string content)
    {
        Assert.Empty(Scan(content));
    }

    [Fact]
    public void Scan_GreekIdn_FlaggedAsAcceptedMixedScript()
    {
        // The corpus pre-flight identified this legitimate IDN (xcursor-plan9) as the one
        // accepted mixed-script detection: Greek is an ASCII-lookalike-prone script.
        var finding = SingleFinding("url=\"https://\u03C0.duncano.de/x\"");

        Assert.Contains("mixes Latin with", finding.Message, StringComparison.Ordinal);
        Assert.Contains("Greek", finding.Message, StringComparison.Ordinal);
    }

    // ===== check 3: fullwidth characters =====

    [Theory]
    // fullwidth lowercase e
    [InlineData("url=\"https://\uFF45xample.com/x\"")]
    // fullwidth a at value start
    [InlineData("pkgname=\uFF41bc")]
    // fullwidth uppercase A
    [InlineData("depends=('\uFF21BC')")]
    public void Scan_FullwidthAsciiLookalikes_Flagged(string content)
    {
        var finding = SingleFinding(content);
        Assert.Contains("fullwidth", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // U+FF5F is just above the fullwidth ASCII range
    [InlineData("url=\"https://example.com/\uFF5Fx\"")]
    // U+FF00 is just below the fullwidth ASCII range
    [InlineData("url=\"https://example.com/\uFF00x\"")]
    public void Scan_CharactersOutsideFullwidthRange_NotFlagged(string content)
    {
        // Neither value matches any check: not hidden, not mixed with lookalike scripts,
        // not fullwidth, and the skeleton is unchanged.
        Assert.Empty(Scan(content));
    }

    // ===== check 4: confusable skeleton =====

    // The mixed-script check fires first whenever Latin is present, so the skeleton check
    // is only reached by values without Latin letters - single-script confusable strings
    // that fold to pure ASCII.
    [Theory]
    // pure Cyrillic confusables in dependency
    [InlineData("depends=('\u0430\u0441\u0435')", "ace")]
    // pure Cyrillic package name
    [InlineData("pkgname='\u0441\u043E\u0440'", "cop")]
    // pure Cyrillic confusables in source
    [InlineData("source=('\u0445\u0435\u0445')", "xex")]
    public void Scan_ConfusableSkeletonFoldingToAscii_Flagged(string content, string skeleton)
    {
        var finding = SingleFinding(content);

        Assert.Contains("resemble ASCII", finding.Message, StringComparison.Ordinal);
        Assert.Contains($"skeleton '{skeleton}'", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // accented Latin is not confusable
    [InlineData("source=(\"https://appli.r\u00E9seau-constellation.ca/x\")")]
    // accented Latin filename
    [InlineData("source=(\"1.6_Versi\u00F3n.tar.gz\")")]
    // accented Latin outside the fields is irrelevant anyway
    [InlineData("pkgdesc=\"Caf\u00E9 tool\u00FCng\"")]
    public void Scan_AccentedLatinValues_NotFlagged(string content)
    {
        Assert.Empty(Scan(content));
    }

    [Fact]
    public void Scan_SkeletonStayingNonAscii_NotFlagged()
    {
        // CJK is not in the confusables table, so the skeleton equals the value.
        Assert.Empty(Scan("source=(\"https://example.com/\u4E2D\u6587.tar.gz\")"));
    }

    // ===== messages and presentation =====

    [Fact]
    public void Scan_NonAsciiValue_MessageRendersCodePointEscapes()
    {
        var finding = SingleFinding("url=\"https://g\u0456thub.com/x\"");

        Assert.Contains("g[U+0456]thub.com", finding.Message, StringComparison.Ordinal);
        Assert.Contains("in url", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_IndentedValueWithNonAscii_SnippetStripsIndentation()
    {
        var finding = Scan("  pkgname=ev\u0430il\n")[0];

        Assert.Equal("pkgname=ev\u0430il", finding.Snippet);
    }
}
