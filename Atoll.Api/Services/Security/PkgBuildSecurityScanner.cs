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

    private static readonly string[] RiskyTools =
    [
        // JavaScript / Node
        "npm", "npx", "yarn", "pnpm", "pnpx", "bun", "node", "deno",
        // Python
        "pip", "pip3", "pipx", "uv", "poetry", "pipenv", "rye", "conda",
        "mamba", "micromamba",
        // Ruby
        "gem",
        // Rust
        "cargo install", "rustup",
        // Go
        "go install",
        // PHP
        "php", "composer",
        // Perl
        "cpan", "cpanm",
        // Haskell
        "cabal install", "stack install",
        // Lua
        "luarocks",
        // Nim
        "nimble install",
        // OCaml
        "opam",
        // Elixir / Erlang
        "mix", "rebar3",
        // C/C++
        "conan", "vcpkg",
        // JVM / Scala / Clojure
        "gradle", "mvn", "sbt", "ant", "lein",
        // .NET
        "dotnet",
        // Swift
        "swift",
        // Julia
        "julia",
        // R
        "Rscript",
        // Downloaders / network tools
        "curl", "wget", "wget2", "aria2c",
        "lftp", "rsync", "scp", "sftp", "fetch",
        // Containers / orchestration / alternative package managers
        "docker", "podman", "kubectl",
        "helm", "snap", "flatpak", "appimage",
        // Version managers
        "nvm", "rvm", "rbenv", "pyenv",
        "gvm", "asdf"
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

            ScanContent(content, isPkgbuild, path, findings);
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

    private static void ScanContent(
        string content,
        bool isPkgbuild,
        string path,
        List<SecurityFinding> findings)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = StripShellComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (TryFindHiddenCharacter(line))
                findings.Add(new SecurityFinding(
                    "hidden-character",
                    FindingSeverity.Critical,
                    "Hidden or bidirectional control characters detected - " +
                    "the visible code may not match what the shell actually executes.",
                    raw.Trim(),
                    path));

            var probe = NormalizeForMatching(line);
            var quotedProbe = ComputeQuotedMask(probe);
            var quotedLine = ComputeQuotedMask(line);

            foreach (var rule in Rules)
            {
                if (!rule.Regex.IsMatch(probe))
                    continue;

                var obfuscated = !rule.Regex.IsMatch(line);
                var severity = obfuscated ? FindingSeverity.Critical : rule.Severity;
                findings.Add(new SecurityFinding(
                    rule.Id,
                    severity,
                    Describe(rule.Id, obfuscated),
                    raw.Trim(),
                    path));
            }

            foreach (var tool in PrivilegeEscalationTools)
            {
                if (!MatchesToolBoundary(probe, tool, quotedProbe))
                    continue;

                var obfuscated = !MatchesToolBoundary(line, tool, quotedLine);
                var message = obfuscated
                    ? $"Privilege escalation tool '{tool}' is invoked via obfuscated shell syntax - " +
                      "the tool name was deliberately hidden, which is a strong sign of malicious intent."
                    : $"Privilege escalation tool '{tool}' is invoked - this runs code as root outside " +
                      "of the package manager's control and can give the package unrestricted access " +
                      "to the whole system.";
                findings.Add(new SecurityFinding(
                    "privilege-escalation",
                    obfuscated ? FindingSeverity.Critical : FindingSeverity.High,
                    message,
                    raw.Trim(),
                    path));
            }

            foreach (var tool in RiskyTools)
            {
                if (!MatchesToolBoundary(probe, tool, quotedProbe))
                    continue;

                var obfuscated = !MatchesToolBoundary(line, tool, quotedLine);
                var message = obfuscated
                    ? $"'{tool}' is invoked via obfuscated shell syntax - the tool name was " +
                      "deliberately hidden, which is a strong sign of malicious intent."
                    : $"'{tool}' is invoked - this fetches/executes external code outside pacman's control.";
                findings.Add(new SecurityFinding(
                    "risky-tool",
                    obfuscated ? FindingSeverity.Critical : FindingSeverity.Medium,
                    message,
                    raw.Trim(),
                    path));
            }
        }

        if (isPkgbuild)
            ScanSourceUrls(content, path, findings);
    }

    private static string Describe(string ruleId, bool obfuscated)
    {
        var description = ruleId switch
        {
            "network-to-shell" => "A download is piped directly into a shell - fetched code executes " +
                                  "without any integrity check.",
            "decode-to-shell" => "Encoded data is decoded and piped into a shell - the executed code " +
                                 "cannot be reviewed.",
            "eval-indirection" => "Dynamic command execution - a command is decoded/evaluated and run, " +
                                  "so its real behavior cannot be reviewed.",
            "command-substitution" => "Dynamic command construction - the effective command is computed " +
                                      "at runtime and cannot be statically resolved.",
            "variable-indirection" => "Bash indirect variable expansion - the referenced variable is " +
                                      "resolved at runtime and cannot be statically resolved.",
            "write-outside-build-root" => "A write targets a path outside the build root - it can modify " +
                                          "system files at build/install time.",
            "network-execution" => "A download is executed or redirected into an interpreter - fetched " +
                                   "code runs outside pacman's control.",
            _ => "Suspicious shell construct detected."
        };

        return obfuscated
            ? description + " The construct was also obfuscated, which is a strong sign of malicious intent."
            : description;
    }

    private static void ScanSourceUrls(string content, string path, List<SecurityFinding> findings)
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
                    findings.Add(new SecurityFinding(
                        "suspicious-source-url",
                        FindingSeverity.Medium,
                        $"Source URL '{url}' points to a binary/archive that cannot be reviewed as text - " +
                        "it may contain malicious code.",
                        line,
                        path));
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
            if (c is '\'' or '"' && i + 1 < line.Length && line[i + 1] == c)
            {
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool MatchesToolBoundary(string text, string tool, bool[]? quotedMask = null)
    {
        var start = 0;
        while (true)
        {
            var idx = text.IndexOf(tool, start, StringComparison.Ordinal);
            if (idx < 0)
                return false;

            if (quotedMask is not null && IsEntirelyQuoted(idx, tool.Length, quotedMask))
            {
                start = idx + tool.Length;
                continue;
            }

            var prevOk = idx == 0 || IsShellBoundary(text[idx - 1]);
            var after = idx + tool.Length;
            var nextOk = after == text.Length || char.IsWhiteSpace(text[after]);
            if (prevOk && nextOk)
                return true;

            start = idx + 1;
        }
    }

    private static bool[] ComputeQuotedMask(string text)
    {
        var mask = new bool[text.Length];
        var stack = new Stack<QuoteRegion>();
        var current = QuoteRegion.Normal;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            switch (current)
            {
                case QuoteRegion.Normal:
                    if (c == '\'')
                    {
                        stack.Push(current);
                        current = QuoteRegion.SingleQuoted;
                    }
                    else if (c == '"')
                    {
                        stack.Push(current);
                        current = QuoteRegion.DoubleQuoted;
                    }
                    else if (c == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }
                    else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
                    {
                        stack.Push(current);
                        current = QuoteRegion.CommandSubstitution;
                        i++;
                    }

                    break;

                case QuoteRegion.SingleQuoted:
                    if (c == '\'') current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                    else mask[i] = true;
                    break;

                case QuoteRegion.DoubleQuoted:
                    if (c == '"')
                    {
                        current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                    }
                    else if (c == '\\' && i + 1 < text.Length)
                    {
                        mask[i + 1] = true;
                        i++;
                    }
                    else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
                    {
                        stack.Push(current);
                        current = QuoteRegion.CommandSubstitution;
                        i++;
                    }
                    else
                    {
                        mask[i] = true;
                    }

                    break;

                case QuoteRegion.CommandSubstitution:
                    if (c == '\'')
                    {
                        stack.Push(current);
                        current = QuoteRegion.SingleQuoted;
                    }
                    else if (c == '"')
                    {
                        stack.Push(current);
                        current = QuoteRegion.DoubleQuoted;
                    }
                    else if (c == '\\' && i + 1 < text.Length)
                    {
                        i++;
                    }
                    else if (c == '$' && i + 1 < text.Length && text[i + 1] == '(')
                    {
                        stack.Push(current);
                        current = QuoteRegion.CommandSubstitution;
                        i++;
                    }
                    else if (c == '(')
                    {
                        stack.Push(current);
                    }
                    else if (c == ')')
                    {
                        current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                    }

                    break;
            }
        }

        return mask;
    }

    private static bool IsEntirelyQuoted(int idx, int length, bool[] quotedMask)
    {
        for (var i = idx; i < idx + length; i++)
            if (!quotedMask[i])
                return false;

        return true;
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

    private enum QuoteRegion
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        CommandSubstitution
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