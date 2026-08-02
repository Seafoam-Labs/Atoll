using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security.Scanning;

public class PkgBuildSourceUrlScannerTests
{
    private static List<SecurityFinding> Scan(string content, string path = "PKGBUILD")
    {
        return PkgBuildSourceUrlScanner.Scan(content, path).ToList();
    }

    private static SecurityFinding SingleFinding(string content, string path = "PKGBUILD")
    {
        var findings = Scan(content, path);
        Assert.That(findings, Has.Count.EqualTo(1), $"Expected exactly one finding, got {findings.Count}");
        return findings[0];
    }

    [Test]
    public void Scan_flags_binary_or_archive_urls_in_source_declarations()
    {
        var findings = PkgBuildSourceUrlScanner.Scan(
            "source=(https://payload.exe https://example.com/source.txt)", "PKGBUILD").ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].RuleId, Is.EqualTo("suspicious-source-url"));
        Assert.That(findings[0].Snippet, Does.Contain("https://payload.exe"));
    }

    [TestCase("https://host.zip")]
    [TestCase("https://host.rar")]
    [TestCase("https://host.7z")]
    [TestCase("https://host.tar.gz")]
    [TestCase("https://host.tar.bz2")]
    [TestCase("https://host.tgz")]
    [TestCase("https://host.exe")]
    [TestCase("https://host.msi")]
    [TestCase("https://host.bin")]
    [TestCase("https://host.EXE", Description = "case-insensitive extension")]
    [TestCase("http://host.zip", Description = "plain http")]
    public void Scan_flags_all_known_binary_or_archive_extensions(string url)
    {
        var findings = Scan($"source=({url})");

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].RuleId, Is.EqualTo("suspicious-source-url"));
    }

    [Test]
    public void Scan_emits_one_finding_per_suspicious_url_on_a_line()
    {
        var findings = Scan("source=(https://a.exe https://b.zip https://c.rar)");

        Assert.That(findings, Has.Count.EqualTo(3));
    }

    [Test]
    public void Scan_finding_has_medium_severity_and_preserves_path()
    {
        var finding = SingleFinding("source=(https://host.exe)", "subdir/PKGBUILD");

        Assert.That(finding.Severity, Is.EqualTo(FindingSeverity.Medium));
        Assert.That(finding.File, Is.EqualTo("subdir/PKGBUILD"));
        Assert.That(finding.RuleId, Is.EqualTo("suspicious-source-url"));
        Assert.That(finding.Message, Does.Contain("https://host.exe"));
    }

    [Test]
    public void Scan_matches_source_anywhere_in_a_line()
    {
        // The scanner matches "source=" anywhere, not just at the start - common for indented declarations.
        var findings = Scan("  source=(https://host.exe)");

        Assert.That(findings, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scan_matches_source_prefixed_by_other_text()
    {
        // e.g. "_source_extra=..." contains "source=" as a substring.
        var findings = Scan("_custom_source=(https://host.exe)");

        Assert.That(findings, Has.Count.EqualTo(1));
    }

    [Test]
    public void Scan_ignores_urls_outside_source_declarations()
    {
        var findings = Scan("url=https://example.com/payload.exe");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Scan_ignores_suspicious_extension_in_url_path()
    {
        // Pitfall guard: the regex only matches when the extension is on the host, never on a URL path.
        // Broadening this breaks the Shelly/Clean end-to-end regression tests.
        var findings = Scan("source=(https://example.com/downloads/payload.exe)");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Scan_ignores_url_with_filename_prefix_using_redirect()
    {
        // Common PKGBUILD pattern: "${name}-${ver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz"
        // The URL ends in .tar.gz, but it's on a path - it must not be flagged.
        var findings = Scan("source=(\"${pkgname}-${pkgver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz\")");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Scan_ignores_plain_text_urls()
    {
        var findings = Scan("source=(https://example.com)");

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void Scan_processes_each_line_independently()
    {
        var findings = Scan("source=(https://a.exe)\nurl=https://b.zip\nsource=(https://c.rar)");

        // Only the two source= lines are flagged.
        Assert.That(findings, Has.Count.EqualTo(2));
    }

    [Test]
    public void Scan_returns_empty_for_empty_content()
    {
        Assert.That(Scan(""), Is.Empty);
    }

    [Test]
    public void Scan_trims_trailing_delimiters_from_url_before_matching()
    {
        // The url value gets ) ] } , ; trimmed - this should still match the suspicious extension.
        var findings = Scan("source=(https://host.zip)");

        Assert.That(findings, Has.Count.EqualTo(1));
    }
}