using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

internal static partial class PkgBuildSourceUrlScanner
{
    public static IEnumerable<SecurityFinding> Scan(string content, string path)
    {
        foreach (var sourceLine in content.Split('\n'))
        {
            var line = sourceLine.Trim();
            if (!line.StartsWith("source", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("source=", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (Match match in HttpRegex().Matches(line))
            {
                var url = match.Value.TrimEnd(')', ']', '}', ',', ';');
                if (!SuspiciousSourceUrl().IsMatch(url))
                    continue;

                var rule = SecurityFindingRules.SuspiciousSourceUrl;
                yield return new SecurityFinding(
                    rule.Id,
                    rule.Severity,
                    string.Format(rule.Description, url),
                    line,
                    path);
            }
        }
    }

    [GeneratedRegex(@"^https?://[^\s/]+\.(zip|rar|7z|tar\.gz|tar\.bz2|tgz|exe|msi|bin)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex SuspiciousSourceUrl();

    [GeneratedRegex(@"https?://[^\s'""]+")]
    private static partial Regex HttpRegex();
}