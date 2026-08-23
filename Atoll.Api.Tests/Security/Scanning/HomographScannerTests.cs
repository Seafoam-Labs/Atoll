using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class HomographScannerTests
{
    private static List<SecurityFinding> Scan(string content, string path = "PKGBUILD")
    {
        return [.. HomographScanner.Scan(content, path)];
    }

    private static SecurityFinding SingleFinding(string content)
    {
        var findings = Scan(content);
        Assert.That(findings, Has.Count.EqualTo(1),
            $"Expected exactly one finding, got {findings.Count}: " +
            string.Join("; ", findings.Select(f => f.Message)));
        return findings[0];
    }

    // ===== scope: fields and value extraction =====

    [TestCase("pkgname=foo\n")]
    [TestCase("pkgver=1.0\n")]
    [TestCase("pkgdesc=A perfectly ASCII description\n")]
    [TestCase("# pkgname=evil\u0430\n", Description = "comment lines are ignored")]
    [TestCase("echo pkgname=evil\u0430\n", Description = "only assignments at line start match")]
    [TestCase("mypkgname=evil\u0430\n", Description = "field names match exactly, not as suffixes")]
    [TestCase("pkgname = evil\u0430\n", Description = "space before '=' is not a valid assignment")]
    public void Non_field_and_ascii_lines_produce_no_findings(string content)
    {
        Assert.That(Scan(content), Is.Empty);
    }

    [TestCase("pkgname=ev\u0430il", Description = "pkgname scalar")]
    [TestCase("pkgname=('ev\u0430il')", Description = "pkgname array")]
    [TestCase("depends=('ev\u0430il')", Description = "depends array")]
    [TestCase("makedepends=('ev\u0430il')", Description = "makedepends array")]
    [TestCase("url=\"https://g\u0456thub.com/x\"", Description = "url scalar")]
    [TestCase("source=(\"https://g\u0456thub.com/x.tar.gz\")", Description = "source array")]
    public void All_phase2_fields_are_checked(string content)
    {
        Assert.That(Scan(content), Has.Count.EqualTo(1));
        Assert.That(Scan(content)[0].RuleId, Is.EqualTo("homograph"));
    }

    [Test]
    public void Indented_assignment_inside_package_function_is_checked()
    {
        var findings = Scan("package() {\n  depends=('pacman' 'ev\u0430il')\n}\n");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].File, Is.EqualTo("PKGBUILD"));
    }

    [Test]
    public void Non_ascii_in_trailing_comment_is_not_flagged()
    {
        var findings = Scan("source=(\"https://example.com/x.tar.gz\") # 中文说明，构建时需要网络\n");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Non_ascii_after_closing_quote_is_a_comment_and_not_flagged()
    {
        var findings = Scan("url=\"https://example.com\" # комментарий\n");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Surrounding_quotes_are_stripped_before_checking()
    {
        // The edge quotes themselves are ASCII; only the value content matters.
        var single = Scan("url='https://g\u0456thub.com/x'");
        var unquoted = Scan("url=https://g\u0456thub.com/x");

        Assert.That(single, Has.Count.EqualTo(1));
        Assert.That(unquoted, Has.Count.EqualTo(1));
        Assert.That(single[0].Message, Is.EqualTo(unquoted[0].Message));
    }

    [Test]
    public void Each_array_element_is_checked()
    {
        var findings = Scan("depends=('ok' '\u0430bc' 'def\u0435')");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("[U+0430]bc").And.Contain("def[U+0435]"));
    }

    [Test]
    public void Array_elements_with_spaces_stay_single_values()
    {
        var findings = Scan("source=('local file with \u0430 spaces.tar.gz')");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("local file with [U+0430] spaces.tar.gz"));
    }

    [Test]
    public void Finding_carries_rule_severity_snippet_and_file()
    {
        var finding = SingleFinding("url=\"https://g\u0456thub.com/x\"");

        Assert.That(finding.RuleId, Is.EqualTo("homograph"));
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(finding.Snippet, Is.EqualTo("url=\"https://g\u0456thub.com/x\""));
        Assert.That(finding.File, Is.EqualTo("PKGBUILD"));
    }

    // ===== check 1: hidden / invisible characters =====

    [TestCase("url=\"https://example.com/\u200Bx\"", Description = "zero-width space")]
    [TestCase("url=\"https://example.com/\u202Ex\"", Description = "bidi override")]
    [TestCase("depends=('a\u200Cb')", Description = "zero-width non-joiner")]
    public void Zero_width_and_bidi_characters_are_flagged(string content)
    {
        var finding = SingleFinding(content);
        Assert.That(finding.Message, Does.Contain("hidden or invisible character"));
    }

    [Test]
    public void Combining_mark_prepended_to_url_scheme_is_flagged()
    {
        // The live corpus false negative: U+0670 (Arabic superscript alef) prepended to
        // the scheme in poweriso-gui. NFC normalization cannot compose it away.
        var finding = SingleFinding("url=\"\u0670http://www.poweriso.com/download.htm\"");

        Assert.That(finding.Message, Does.Contain("U+0670"));
        Assert.That(finding.Message, Does.Contain("hidden or invisible character"));
    }

    [Test]
    public void Hidden_character_message_names_the_code_point()
    {
        var finding = SingleFinding("url=\"https://example.com/\u200Bx\"");

        Assert.That(finding.Message, Does.Contain("U+200B"));
    }

    [Test]
    public void Hidden_character_takes_precedence_over_other_checks()
    {
        // The value also mixes Latin with Cyrillic, but the hidden check fires first.
        var finding = SingleFinding("url=\"https://\u200Bg\u0456thub.com\"");

        Assert.That(finding.Message, Does.Contain("hidden or invisible character"));
    }

    // ===== check 2: mixed scripts =====

    [TestCase("url=\"https://g\u0456thub.com/x\"", "Cyrillic", Description = "Cyrillic i lookalike in host")]
    [TestCase("url=\"https://g\u03B9thub.com/x\"", "Greek", Description = "Greek iota lookalike in host")]
    [TestCase("depends=('a\u0562c')", "Armenian", Description = "Armenian letter in dependency")]
    public void Latin_mixed_with_lookalike_scripts_is_flagged(string content, string scriptName)
    {
        var finding = SingleFinding(content);

        Assert.That(finding.Message, Does.Contain("mixes Latin with"));
        Assert.That(finding.Message, Does.Contain(scriptName));
    }

    [TestCase("source=(\"https://开发자양반.info/x.tar.gz\")", Description = "CJK and Hangul cannot spoof ASCII")]
    [TestCase("source=(\"https://例え.测试/ファイル\")", Description = "pure CJK IDN without Latin")]
    [TestCase("pkgname='\u0430\u0431\u0432'", Description = "pure Cyrillic without Latin is not mixed")]
    public void CJK_Hangul_and_single_script_values_are_not_flagged(string content)
    {
        Assert.That(Scan(content), Is.Empty);
    }

    [Test]
    public void Greek_idn_is_flagged_as_known_accepted_detection()
    {
        // The corpus pre-flight identified this legitimate IDN (xcursor-plan9) as the one
        // accepted mixed-script detection: Greek is an ASCII-lookalike-prone script.
        var finding = SingleFinding("url=\"https://\u03C0.duncano.de/x\"");

        Assert.That(finding.Message, Does.Contain("mixes Latin with"));
        Assert.That(finding.Message, Does.Contain("Greek"));
    }

    // ===== check 3: fullwidth characters =====

    [TestCase("url=\"https://\uFF45xample.com/x\"", Description = "fullwidth lowercase e")]
    [TestCase("pkgname=\uFF41bc", Description = "fullwidth a at value start")]
    [TestCase("depends=('\uFF21BC')", Description = "fullwidth uppercase A")]
    public void Fullwidth_ascii_lookalikes_are_flagged(string content)
    {
        var finding = SingleFinding(content);
        Assert.That(finding.Message, Does.Contain("fullwidth"));
    }

    [TestCase("url=\"https://example.com/\uFF5Fx\"", Description = "U+FF5F is just above the fullwidth ASCII range")]
    [TestCase("url=\"https://example.com/\uFF00x\"", Description = "U+FF00 is just below the fullwidth ASCII range")]
    public void Characters_outside_the_fullwidth_range_are_not_flagged_by_that_check(string content)
    {
        // Neither value matches any check: not hidden, not mixed with lookalike scripts,
        // not fullwidth, and the skeleton is unchanged.
        Assert.That(Scan(content), Is.Empty);
    }

    // ===== check 4: confusable skeleton =====

    // The mixed-script check fires first whenever Latin is present, so the skeleton check
    // is only reached by values without Latin letters - single-script confusable strings
    // that fold to pure ASCII.
    [TestCase("depends=('\u0430\u0441\u0435')", "ace", Description = "pure Cyrillic confusables in dependency")]
    [TestCase("pkgname='\u0441\u043E\u0440'", "cop", Description = "pure Cyrillic package name")]
    [TestCase("source=('\u0445\u0435\u0445')", "xex", Description = "pure Cyrillic confusables in source")]
    public void Confusable_skeletons_that_fold_to_ascii_are_flagged(string content, string skeleton)
    {
        var finding = SingleFinding(content);

        Assert.That(finding.Message, Does.Contain("resemble ASCII"));
        Assert.That(finding.Message, Does.Contain($"skeleton '{skeleton}'"));
    }

    [TestCase("source=(\"https://appli.r\u00E9seau-constellation.ca/x\")", Description = "accented Latin is not confusable")]
    [TestCase("source=(\"1.6_Versi\u00F3n.tar.gz\")", Description = "accented Latin filename")]
    [TestCase("pkgdesc=\"Caf\u00E9 tool\u00FCng\"", Description = "accented Latin outside the fields is irrelevant anyway")]
    public void Accented_latin_values_are_not_flagged(string content)
    {
        Assert.That(Scan(content), Is.Empty);
    }

    [Test]
    public void Skeleton_that_stays_non_ascii_is_not_flagged_by_the_skeleton_check()
    {
        // CJK is not in the confusables table, so the skeleton equals the value.
        Assert.That(Scan("source=(\"https://example.com/\u4E2D\u6587.tar.gz\")"), Is.Empty);
    }

    // ===== messages and presentation =====

    [Test]
    public void Message_renders_non_ascii_as_code_point_escapes()
    {
        var finding = SingleFinding("url=\"https://g\u0456thub.com/x\"");

        Assert.That(finding.Message, Does.Contain("g[U+0456]thub.com"));
        Assert.That(finding.Message, Does.Contain("in url"));
    }

    [Test]
    public void Non_ascii_in_value_is_described_even_when_the_line_is_indented()
    {
        var finding = Scan("  pkgname=ev\u0430il\n")[0];

        Assert.That(finding.Snippet, Is.EqualTo("pkgname=ev\u0430il"));
    }
}
