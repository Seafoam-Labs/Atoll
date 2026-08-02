using System.Text;
using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security;

public sealed partial class PkgBuildSecurityScanner : IPackageSecurityScanner
{
    private static readonly string[] ScriptExtensions =
    [
        ".sh", ".bash", ".install", ".hook", ".py", ".pl", ".rb", ".service", ".csh", ".zsh"
    ];

    private static readonly string[] PrivilegeEscalationTools =
    [
        "sudo", "sudoedit", "doas", "pkexec", "run0", "su"
    ];

    private static readonly Rule[] Rules =
    [
        new("network-to-shell", FindingSeverity.Critical,
            @"(^|[\s;&|`(])(curl|wget|wget2|aria2c|fetch|lynx|httpie|http)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh|fish)\b"),

        new("decode-to-shell", FindingSeverity.Critical,
            @"(base64|xxd|openssl\s+enc|printf|echo)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh)\b"),

        new("eval-indirection", FindingSeverity.Critical,
            @"(^|[\s;&|`(])(eval|source|\.)\s+(\$\(|`|base64|echo|printf)"),

        new("command-substitution", FindingSeverity.Medium,
            @"\$\(|`",
            RegexOptions.None),

        new("variable-indirection", FindingSeverity.Medium,
            @"\$\{!"),

        new("write-outside-build-root", FindingSeverity.High,
            @"(^|[\s;&|`(])(>|>>|tee)\s*(/etc/|/usr/|/bin/|/sbin/|/var/|/root/|/home/|/opt/|/boot/|/lib/)"),

        new("network-execution", FindingSeverity.High,
            @"(^|[\s;&|`(])(curl|wget|wget2|aria2c|fetch)\b[^|;\n]*(\||;|&&|\s>\s*&?1?)\s*(sh|bash|zsh|dash|ksh|eval|python|perl|ruby|node)\b")
    ];


    public ScanResult Scan(IReadOnlyDictionary<string, string> files)
    {
        var findings = new List<SecurityFinding>();

        foreach (var (path, content) in files)
        {
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var isPkgbuild = path.Equals("PKGBUILD", StringComparison.OrdinalIgnoreCase);
            var isScript = isPkgbuild || IsScriptLike(path);

            if (!isScript)
                continue;

            ScanContent(content, isPkgbuild, findings);
        }

        return new ScanResult(DecideStatus(findings), findings);
    }

    [GeneratedRegex(@"^(https?|ftp)://[^\s/]+\.(zip|rar|7z|tar\.gz|tar\.bz2|tgz|exe|msi|bin)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex SuspiciousSourceUrl();

    [GeneratedRegex(@"https?://[^\s'""]+")]
    private static partial Regex HttpRegex();

    private static SecurityStatus DecideStatus(IReadOnlyList<SecurityFinding> findings)
    {
        return findings.Any(f => f.Severity is FindingSeverity.Critical or FindingSeverity.High)
            ? SecurityStatus.Flagged
            : SecurityStatus.Verified;
    }

    private static bool IsScriptLike(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("PKGBUILD", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(name);
        return ScriptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private static void ScanContent(string content, bool isPkgbuild, List<SecurityFinding> findings)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = StripShellComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (TryFindHiddenCharacter(line))
                findings.Add(new SecurityFinding("hidden-character", FindingSeverity.Critical));

            var probe = NormalizeForMatching(line);

            foreach (var rule in Rules)
            {
                if (!rule.Regex.IsMatch(probe))
                    continue;

                var obfuscated = !rule.Regex.IsMatch(line);
                var severity = obfuscated ? FindingSeverity.Critical : rule.Severity;
                findings.Add(new SecurityFinding(rule.Id, severity));
            }

            foreach (var tool in PrivilegeEscalationTools)
            {
                if (!MatchesToolBoundary(probe, tool))
                    continue;

                var obfuscated = !MatchesToolBoundary(line, tool);
                findings.Add(new SecurityFinding(
                    "privilege-escalation",
                    obfuscated ? FindingSeverity.Critical : FindingSeverity.High));
            }
        }

        if (isPkgbuild)
            ScanSourceUrls(content, findings);
    }

    private static void ScanSourceUrls(string content, List<SecurityFinding> findings)
    {
        var sourceLines = content.Split('\n');
        for (var i = 0; i < sourceLines.Length; i++)
        {
            var line = sourceLines[i].Trim();
            if (!line.StartsWith("source", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("source=", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (Match match in HttpRegex().Matches(line))
            {
                var url = match.Value.TrimEnd(')', ']', '}', ',', ';');
                if (SuspiciousSourceUrl().IsMatch(url))
                    findings.Add(new SecurityFinding("suspicious-source-url", FindingSeverity.Medium));
            }
        }
    }

    private static string NormalizeForMatching(string line)
    {
        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && i + 1 < line.Length)
            {
                var next = line[i + 1];
                if (next != '\\' && !char.IsWhiteSpace(next))
                    continue;
            }

            // Empty quote pairs can split a command name without changing the
            // shell token (for example, c''url). Preserve non-empty quoted
            // values such as the 'sudo' entry in a PKGBUILD dependency array.
            if ((c is '\'' or '"') && i + 1 < line.Length && line[i + 1] == c)
            {
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsWordChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_';
    }

    private static bool MatchesToolBoundary(string text, string tool)
    {
        var start = 0;
        while (true)
        {
            var idx = text.IndexOf(tool, start, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            var prevOk = idx == 0 || IsShellBoundary(text[idx - 1]);
            var after = idx + tool.Length;
            var nextOk = after == text.Length || char.IsWhiteSpace(text[after]);
            if (prevOk && nextOk)
                return true;

            start = idx + 1;
        }
    }

    private static bool IsShellBoundary(char c)
    {
        return c is ' ' or '\t' or '\n' or '\r' or (char)0x0B or (char)0x0C
            or ';' or '&' or '|' or '`' or '(';
    }

    private static bool TryFindHiddenCharacter(string line)
    {
        for (var i = 0; i < line.Length;)
        {
            var codepoint = char.ConvertToUtf32(line, i);
            if (IsHiddenCodepoint(codepoint))
                return true;

            i += char.IsSurrogatePair(line, i) ? 2 : 1;
        }

        return false;
    }

    private static bool IsHiddenCodepoint(int c)
    {
        switch (c)
        {
            case 0x200B or 0x200C or 0x200D or 0xFEFF:
            case >= 0x202A and <= 0x202E:
            case >= 0x2066 and <= 0x2069:
                return true;
        }

        if (c != '\t' && c != '\n' && c != '\r' && IsControlRange(c))
            return true;

        return false;
    }

    private static bool IsControlRange(int cp)
    {
        return cp is < 0x20 or >= 0x7F and <= 0x9F;
    }

    private static string StripShellComment(string line)
    {
        var inSingleQ = false;
        var inDoubleQ = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            switch (c)
            {
                case '"' when !inSingleQ:
                    inDoubleQ = !inDoubleQ;
                    break;
                case '\'' when !inDoubleQ:
                    inSingleQ = !inSingleQ;
                    break;
                case '#' when !inSingleQ && !inDoubleQ:
                    return line[..i];
            }
        }

        return line;
    }

    private sealed record Rule(
        string Id,
        FindingSeverity Severity,
        string Pattern,
        RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}