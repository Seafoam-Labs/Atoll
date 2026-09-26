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
        return Assert.Single(Scan(content, path));
    }

    [Fact]
    public void Scan_SourceDeclarationBinaryArchiveUrls_FlagsSuspiciousSourceUrl()
    {
        var findings = PkgBuildSourceUrlScanner.Scan(
            "source=(https://payload.exe https://example.com/source.txt)", "PKGBUILD").ToList();

        var finding = Assert.Single(findings);
        Assert.Equal("suspicious-source-url", finding.RuleId);
        Assert.Contains("https://payload.exe", finding.Snippet, StringComparison.Ordinal);
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
    public void Scan_KnownBinaryArchiveExtensions_FlagsSuspiciousSourceUrl(string url)
    {
        var findings = Scan($"source=({url})");

        var finding = Assert.Single(findings);
        Assert.Equal("suspicious-source-url", finding.RuleId);
    }

    [Fact]
    public void Scan_MultipleSuspiciousUrlsOnOneLine_EmitsOneFindingPerUrl()
    {
        var findings = Scan("source=(https://a.exe https://b.zip https://c.rar)");

        Assert.Equal(3, findings.Count);
    }

    [Fact]
    public void Scan_SuspiciousUrl_ReportsMediumSeverityAndFilePath()
    {
        var finding = SingleFinding("source=(https://host.exe)", "subdir/PKGBUILD");

        Assert.Equal(FindingSeverity.Medium, finding.Severity);
        Assert.Equal("subdir/PKGBUILD", finding.File);
        Assert.Equal("suspicious-source-url", finding.RuleId);
        Assert.Contains("https://host.exe", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_IndentedSourceDeclaration_StillMatches()
    {
        // The scanner matches "source=" anywhere, not just at the start - common for indented declarations.
        var findings = Scan("  source=(https://host.exe)");

        Assert.Single(findings);
    }

    [Fact]
    public void Scan_SourceTokenPrefixedByOtherText_StillMatches()
    {
        // e.g. "_source_extra=..." contains "source=" as a substring.
        var findings = Scan("_custom_source=(https://host.exe)");

        Assert.Single(findings);
    }

    [Fact]
    public void Scan_UrlOutsideSourceDeclaration_Ignored()
    {
        var findings = Scan("url=https://example.com/payload.exe");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_SuspiciousExtensionOnUrlPath_Ignored()
    {
        // Pitfall guard: the regex only matches when the extension is on the host, never on a URL path.
        // Broadening this breaks the Shelly/Clean end-to-end regression tests.
        var findings = Scan("source=(https://example.com/downloads/payload.exe)");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_FilenamePrefixedRedirectUrl_Ignored()
    {
        // Common PKGBUILD pattern: "${name}-${ver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz"
        // The URL ends in .tar.gz, but it's on a path - it must not be flagged.
        var findings = Scan("source=(\"${pkgname}-${pkgver}.tar.gz::https://github.com/x/y/archive/v1.0.tar.gz\")");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_UrlWithoutSuspiciousExtension_Ignored()
    {
        var findings = Scan("source=(https://example.com)");

        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_MultipleLines_FlagsOnlySourceDeclarations()
    {
        var findings = Scan("source=(https://a.exe)\nurl=https://b.zip\nsource=(https://c.rar)");

        // Only the two source= lines are flagged.
        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Scan_EmptyContent_ReturnsEmpty()
    {
        Assert.Empty(Scan(""));
    }

    [Fact]
    public void Scan_TrailingDelimiters_TrimmedBeforeMatching()
    {
        // The url value gets ) ] } , ; trimmed - this should still match the suspicious extension.
        var findings = Scan("source=(https://host.zip)");

        Assert.Single(findings);
    }
}