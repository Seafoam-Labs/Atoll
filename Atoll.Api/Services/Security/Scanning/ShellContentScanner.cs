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
        new(SecurityFindingRules.NetworkToShell,
            @"(^|[\s;&|`(])(curl|wget|wget2|aria2c|fetch|lynx|httpie|http)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh|fish)\b"),
        new(SecurityFindingRules.DecodeToShell, @"(base64|xxd|openssl\s+enc|printf|echo)\b[^|;\n]*\|\s*(sh|bash|zsh|dash|ksh)\b"),
        new(SecurityFindingRules.EvalIndirection, @"(^|[\s;&|`(])(eval|source|\.)\s+(\$\(|`|base64|echo|printf)"),
        new(SecurityFindingRules.CommandSubstitution, @"\$\(|`", RegexOptions.None),
        new(SecurityFindingRules.VariableIndirection, @"\$\{!"),
        new(SecurityFindingRules.WriteOutsideBuildRoot,
            @"(^|[\s;&|`(])(>|>>|tee)\s*(/etc/|/usr/|/bin/|/sbin/|/var/|/root/|/home/|/opt/|/boot/|/lib/)"),
        new(SecurityFindingRules.NetworkExecution,
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
                yield return Finding(SecurityFindingRules.HiddenCharacter, rawLine, path);

            var normalized = ShellSyntax.NormalizeForMatching(line);
            foreach (var rule in Rules)
            {
                if (!rule.Regex.IsMatch(normalized))
                    continue;

                var obfuscated = !rule.Regex.IsMatch(line);
                yield return Finding(rule.Definition, rawLine, path,
                    obfuscated ? FindingSeverity.Critical : null,
                    obfuscated
                        ? rule.Definition.Description + " The construct was also obfuscated, which is a strong sign of malicious intent."
                        : null);
            }

            foreach (var tool in PrivilegeEscalationTools)
            {
                if (!ShellSyntax.MatchesUnquotedTool(normalized, tool))
                    continue;

                var obfuscated = !ShellSyntax.MatchesUnquotedTool(line, tool);
                var rule = SecurityFindingRules.PrivilegeEscalation;
                var message = obfuscated
                    ? $"Privilege escalation tool '{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent."
                    : string.Format(rule.Description, tool);
                yield return Finding(rule, rawLine, path, obfuscated ? FindingSeverity.Critical : null, message);
            }

            foreach (var tool in RiskyTools)
            {
                if (!ShellSyntax.MatchesUnquotedTool(normalized, tool))
                    continue;

                var obfuscated = !ShellSyntax.MatchesUnquotedTool(line, tool);
                var rule = SecurityFindingRules.RiskyTool;
                var message = obfuscated
                    ? $"'{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent."
                    : string.Format(rule.Description, tool);
                yield return Finding(rule, rawLine, path, obfuscated ? FindingSeverity.Critical : null, message);
            }
        }
    }

    private static SecurityFinding Finding(
        SecurityFindingRule rule,
        string rawLine,
        string path,
        FindingSeverity? severity = null,
        string? message = null)
    {
        return new SecurityFinding(rule.Id, severity ?? rule.Severity, message ?? rule.Description, rawLine.Trim(), path);
    }

    private sealed record Rule(SecurityFindingRule Definition, string Pattern, RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}