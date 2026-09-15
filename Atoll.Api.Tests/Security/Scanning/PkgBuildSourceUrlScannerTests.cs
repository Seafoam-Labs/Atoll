using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Scanning;
using Xunit;

namespace Atoll.Api.Tests.Security.Scanning;

public class PkgBuildSourceUrlScannerTests
{
    private static List<SecurityFinding> Scan(string content, string path = "PKGBUILD")
    {
        return [.. PkgBuildSourceUrlScanner.Scan(content, path)];
    }

    private static SecurityFinding SingleFinding(string content, string path = "PKGBUILD")
    {
        var findings = Scan(content, path);
        Assert.Single(findings);
        return findings[0];
    }

    [Fact]
    public void Scan_flags_binary_or_archive_urls_in_source_declarations()
    {
        var findings = PkgBuildSourceUrlScanner.Scan(
            "source=(https://payload.exe https://example.com/source.txt)", "PKGBUILD").ToList();

        Assert.Single(findings);
        Assert.Equal("suspicious-source-url", findings[0].RuleId);
        Assert.Contains("https://payload.exe", findings[0].Snippet);
    }

    [Theory]
    [InlineData("https://host.zip")]
    [InlineData("https://host.rar")]
    [InlineData("https://host.7z")]
    [InlineData("https://host.tar.gz")]
    [InlineData("https://host.tar.bz2")]
    [InlineData("https://host.tgz")]
    [InlineData("https://host.exe")]
    [InlineData("https://host.msi")]
    [InlineData("https://host.bin")]
    // case-insensitive extension
    [InlineData("https://host.EXE")]
    // plain http
    [InlineData("http://host.zip")]
    public void Scan_flags_all_known_binary_or_archive_extensions(string url)
    {
        var findings = Scan($"source=({url})");

        Assert.Single(findings);
        Assert.Equal("suspicious-source-url", findings[0].RuleId);
    }

    [Fact]
    public void Scan_emits_one_finding_per_suspicious_url_on_a_line()
    {
        var findings = Scan("source=(https://a.exe https://b.zip https://c.rar)");

        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public void Scan_finding_has_medium_severity_and_preserves_path()
    {
        var finding = SingleFinding("source=(https://host.exe)", "subdir/PKGBUILD");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("subdir/PKGBUILD", finding.File);
        Assert.Equal("suspicious-source-url", finding.RuleId);
        Assert.Contains("https://host.exe", finding.Message);
    }

    [Fact]
    public void Scan_matches_source_anywhere_in_a_line()
    {
        // The scanner matches "source=" anywhere, not just at the start - common for indented declarations.
        var findings = Scan("  source=(https://host.exe)");

        Assert.Single(findings);
    }

    [Fact]
    public void Scan_matches_source_prefixed_by_other_text()
    {
        // e.g. "_source_extra=..." contains "source=" as a substring.
        var findings = Scan("_custom_source=(https://host.exe)");

        Assert.Single(findings);
    }

    [Fact]
    public void Scan_ignores_urls_outside_source_declarations()
    {
        var findings = Scan("url=https://example.com/payload.exe");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_ignores_suspicious_extension_in_url_path()
    {
        // Pitfall guard: the regex only matches when the extension is on the host, never on a URL path.
        // Broadening this breaks the Shelly/Clean end-to-end regression tests.
        var findings = Scan("source=(https://example.com/downloads/payload.exe)");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_ignores_url_with_filename_prefix_using_redirect()
    {
        // Common PKGBUILD pattern: "${name}-${ver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz"
        // The URL ends in .tar.gz, but it's on a path - it must not be flagged.
        var findings = Scan("source=(\"${pkgname}-${pkgver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz\")");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_ignores_plain_text_urls()
    {
        var findings = Scan("source=(https://example.com)");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_processes_each_line_independently()
    {
        var findings = Scan("source=(https://a.exe)\nurl=https://b.zip\nsource=(https://c.rar)");

        // Only the two source= lines are flagged.
        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Scan_returns_empty_for_empty_content()
    {
        Assert.Empty(Scan(""));
    }

    [Fact]
    public void Scan_trims_trailing_delimiters_from_url_before_matching()
    {
        // The url value gets ) ] } , ; trimmed - this should still match the suspicious extension.
        var findings = Scan("source=(https://host.zip)");

        Assert.Single(findings);
    }
}