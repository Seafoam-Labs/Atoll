using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

internal static class ShellContentScanner
{
    private static readonly string[] PrivilegeEscalationTools = ["sudo", "sudoedit", "doas", "pkexec", "run0", "su"];

    private static readonly string[] RiskyTools =
    [
        "npm", "npx", "yarn", "pnpm", "pnpx", "bun", "node", "deno",
        "pip", "pip3", "pipx", "uv", "poetry", "pipenv", "rye", "conda", "mamba", "micromamba",
        "gem", "cargo install", "rustup", "go install", "php", "composer", "cpan", "cpanm",
        "cabal install", "stack install", "luarocks", "nimble install", "opam", "mix", "rebar3",
        "conan", "vcpkg", "gradle", "mvn", "sbt", "ant", "lein", "dotnet", "swift", "julia", "Rscript",
        "curl", "wget", "wget2", "aria2c", "lftp", "rsync", "scp", "sftp", "fetch",
        "docker", "podman", "kubectl", "helm", "snap", "flatpak", "appimage",
        "nvm", "rvm", "rbenv", "pyenv", "gvm", "asdf"
    ];

    private static readonly Rule[] Rules =
    [
        new("network-to-shell", FindingSeverity.Critical,
            @"(^|[\s;&|`(])(curl|wget|wget2|aria2c|fetch|lynx|httpie|http)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh|fish)\b"),
        new("decode-to-shell", FindingSeverity.Critical, @"(base64|xxd|openssl\s+enc|printf|echo)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh)\b"),
        new("eval-indirection", FindingSeverity.Critical, @"(^|[\s;&|`(])(eval|source|\.)\s+(\$\(|`|base64|echo|printf)"),
        new("command-substitution", FindingSeverity.Medium, @"\$\(|`", RegexOptions.None),
        new("variable-indirection", FindingSeverity.Medium, @"\$\{!"),
        new("write-outside-build-root", FindingSeverity.High,
            @"(^|[\s;&|`(])(>|>>|tee)\s*(/etc/|/usr/|/bin/|/sbin/|/var/|/root/|/home/|/opt/|/boot/|/lib/)"),
        new("network-execution", FindingSeverity.High,
            @"(^|[\s;&|`(])(curl|wget|wget2|aria2c|fetch)\b[^|;\n]*(\||;|&&|\s>\s*&?1?)\s*(sh|bash|zsh|dash|ksh|eval|python|perl|ruby|node)\b")
    ];

    public static IEnumerable<SecurityFinding> Scan(string content, string path)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = ShellSyntax.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (ShellSyntax.ContainsHiddenCharacter(line))
                yield return Finding("hidden-character", FindingSeverity.Critical,
                    "Hidden or bidirectional control characters detected - the visible code may not match what the shell actually executes.",
                    rawLine, path);

            var normalized = ShellSyntax.NormalizeForMatching(line);
            foreach (var rule in Rules)
            {
                if (!rule.Regex.IsMatch(normalized))
                    continue;

                var obfuscated = !rule.Regex.IsMatch(line);
                yield return Finding(rule.Id, obfuscated ? FindingSeverity.Critical : rule.Severity, Describe(rule.Id, obfuscated), rawLine,
                    path);
            }

            foreach (var tool in PrivilegeEscalationTools)
            {
                if (!ShellSyntax.MatchesUnquotedTool(normalized, tool))
                    continue;

                var obfuscated = !ShellSyntax.MatchesUnquotedTool(line, tool);
                var message = obfuscated
                    ? $"Privilege escalation tool '{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent."
                    : $"Privilege escalation tool '{tool}' is invoked - this runs code as root outside of the package manager's control and can give the package unrestricted access to the whole system.";
                yield return Finding("privilege-escalation", obfuscated ? FindingSeverity.Critical : FindingSeverity.High, message, rawLine,
                    path);
            }

            foreach (var tool in RiskyTools)
            {
                if (!ShellSyntax.MatchesUnquotedTool(normalized, tool))
                    continue;

                var obfuscated = !ShellSyntax.MatchesUnquotedTool(line, tool);
                var message = obfuscated
                    ? $"'{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent."
                    : $"'{tool}' is invoked - this fetches/executes external code outside pacman's control.";
                yield return Finding("risky-tool", obfuscated ? FindingSeverity.Critical : FindingSeverity.Medium, message, rawLine, path);
            }
        }
    }

    private static SecurityFinding Finding(string ruleId, FindingSeverity severity, string message, string rawLine, string path)
    {
        return new SecurityFinding(ruleId, severity, message, rawLine.Trim(), path);
    }

    private static string Describe(string ruleId, bool obfuscated)
    {
        var description = ruleId switch
        {
            "network-to-shell" => "A download is piped directly into a shell - fetched code executes without any integrity check.",
            "decode-to-shell" => "Encoded data is decoded and piped into a shell - the executed code cannot be reviewed.",
            "eval-indirection" =>
                "Dynamic command execution - a command is decoded/evaluated and run, so its real behavior cannot be reviewed.",
            "command-substitution" =>
                "Dynamic command construction - the effective command is computed at runtime and cannot be statically resolved.",
            "variable-indirection" =>
                "Bash indirect variable expansion - the referenced variable is resolved at runtime and cannot be statically resolved.",
            "write-outside-build-root" =>
                "A write targets a path outside the build root - it can modify system files at build/install time.",
            "network-execution" => "A download is executed or redirected into an interpreter - fetched code runs outside pacman's control.",
            _ => "Suspicious shell construct detected."
        };

        return obfuscated ? description + " The construct was also obfuscated, which is a strong sign of malicious intent." : description;
    }

    private sealed record Rule(string Id, FindingSeverity Severity, string Pattern, RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}