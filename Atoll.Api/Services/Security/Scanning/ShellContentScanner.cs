using System.Text;
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

    // Interpreters that make a quoted-delimiter heredoc body live: piping the body into
    // one of these executes it despite the quoting. Used as a conservative guard - when in
    // doubt the body is scanned as ordinary code.
    private static readonly string[] HeredocPipeTargets =
    [
        "sh", "bash", "zsh", "dash", "ksh", "fish",
        "python", "python2", "python3", "perl", "ruby", "node", "eval"
    ];

    public static IEnumerable<SecurityFinding> Scan(string content, string path)
    {
        Heredoc? activeHeredoc = null;
        var pendingHeredocs = new Queue<Heredoc>();

        foreach (var rawLine in content.Split('\n'))
        {
            if (activeHeredoc is not null)
            {
                var bodyLine = rawLine.TrimEnd('\r');
                var candidate = activeHeredoc.StripTabs ? bodyLine.TrimStart('\t') : bodyLine;
                if (candidate == activeHeredoc.Delimiter)
                {
                    activeHeredoc = pendingHeredocs.Count > 0 ? pendingHeredocs.Dequeue() : null;
                    continue;
                }

                // Heredoc bodies are data, not code lines: no comment stripping.
                foreach (var finding in ScanLine(bodyLine, rawLine, path))
                    if (!activeHeredoc.Suppress || !IsHeredocSuppressedRule(finding.RuleId))
                        yield return finding;

                continue;
            }

            var line = ShellSyntax.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var positions = ShellSyntax.ComputeQuotePositions(line);
            foreach (var finding in ScanLine(line, positions, rawLine, path))
                yield return finding;

            foreach (var declaration in ParseHeredocDeclarations(line, positions))
            {
                var suppress = declaration.Quoted && !PipesIntoInterpreter(line, declaration.End);
                var heredoc = new Heredoc(declaration.Delimiter, declaration.StripTabs, suppress);
                if (activeHeredoc is null)
                    activeHeredoc = heredoc;
                else
                    pendingHeredocs.Enqueue(heredoc);
            }
        }
    }

    private static IEnumerable<SecurityFinding> ScanLine(string line, string rawLine, string path)
    {
        return ScanLine(line, ShellSyntax.ComputeQuotePositions(line), rawLine, path);
    }

    private static IEnumerable<SecurityFinding> ScanLine(
        string line,
        ShellSyntax.QuotePosition[] positions,
        string rawLine,
        string path)
    {
        if (line.Length == 0)
            yield break;

        if (ShellSyntax.ContainsHiddenCharacter(line))
            yield return Finding(SecurityFindingRules.HiddenCharacter, rawLine, path);

        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(line);
        foreach (var rule in Rules)
        {
            var match = rule.Regex.Match(normalized);
            if (!match.Success)
                continue;

            if (IsInertQuotedMatch(rule, match, positions, sourceIndices))
                continue;

            var obfuscated = !rule.Regex.IsMatch(line) &&
                             !ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, match.Index, match.Length);
            yield return Finding(rule.Definition, rawLine, path,
                obfuscated ? FindingSeverity.Critical : null,
                obfuscated
                    ? rule.Definition.Description + " The construct was also obfuscated, which is a strong sign of malicious intent."
                    : null);
        }

        foreach (var tool in PrivilegeEscalationTools)
        {
            var index = ShellSyntax.FindUnquotedTool(normalized, tool);
            if (index < 0)
                continue;

            if (ShellSyntax.MatchesUnquotedTool(line, tool))
            {
                var rule = SecurityFindingRules.PrivilegeEscalation;
                yield return Finding(rule, rawLine, path, message: string.Format(rule.Description, tool));
                continue;
            }

            // Only visible after de-obfuscation. A match that maps entirely inside quoted
            // regions of the original line is an escape-stripping artifact (display text
            // like "\$(sudo ...)" where the backslash prevents execution), not hidden
            // intent: suppress it instead of escalating.
            if (ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, index, tool.Length))
                continue;

            yield return Finding(SecurityFindingRules.PrivilegeEscalation, rawLine, path, FindingSeverity.Critical,
                $"Privilege escalation tool '{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent.");
        }

        foreach (var tool in RiskyTools)
        {
            var index = ShellSyntax.FindUnquotedTool(normalized, tool);
            if (index < 0)
                continue;

            if (ShellSyntax.MatchesUnquotedTool(line, tool))
            {
                var rule = SecurityFindingRules.RiskyTool;
                yield return Finding(rule, rawLine, path, message: string.Format(rule.Description, tool));
                continue;
            }

            // See the privilege-escalation loop: quoted escape-stripping artifacts are
            // suppressed, everything else that only appears after de-obfuscation escalates.
            if (ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, index, tool.Length))
                continue;

            yield return Finding(SecurityFindingRules.RiskyTool, rawLine, path, FindingSeverity.Critical,
                $"'{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent.");
        }
    }

    /// <summary>
    ///     The shell performs no expansion inside single quotes, and an escaped '$' never
    ///     expands, so such matches of the expansion rules are inert text. Redirects and 'tee'
    ///     are inert inside any quoted string. Double-quoted expansions still execute and stay
    ///     flagged.
    /// </summary>
    private static bool IsInertQuotedMatch(
        Rule rule,
        Match match,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        var ruleId = rule.Definition.Id;
        if (ruleId == SecurityFindingRules.WriteOutsideBuildRoot.Id)
        {
            var redirect = match.Groups[2];
            var position = positions[sourceIndices[redirect.Index]];
            if (position.Region is ShellSyntax.QuoteRegion.SingleQuoted or ShellSyntax.QuoteRegion.DoubleQuoted)
                return true;

            // An escaped operator does not redirect; an escaped 'tee' is still a command.
            return position.Escaped && !redirect.Value.Equals("tee", StringComparison.OrdinalIgnoreCase);
        }

        if (ruleId == SecurityFindingRules.CommandSubstitution.Id ||
            ruleId == SecurityFindingRules.VariableIndirection.Id)
        {
            var position = positions[sourceIndices[match.Index]];
            return position.Region == ShellSyntax.QuoteRegion.SingleQuoted || position.Escaped;
        }

        return false;
    }

    private static bool IsHeredocSuppressedRule(string ruleId)
    {
        return ruleId == SecurityFindingRules.CommandSubstitution.Id ||
               ruleId == SecurityFindingRules.VariableIndirection.Id;
    }

    private static IEnumerable<HeredocDeclaration> ParseHeredocDeclarations(
        string line,
        ShellSyntax.QuotePosition[] positions)
    {
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] != '<' || line[i + 1] != '<')
                continue;

            if (positions[i].Escaped || positions[i].Region != ShellSyntax.QuoteRegion.Normal)
            {
                i++;
                continue;
            }

            if (i + 2 < line.Length && line[i + 2] == '<')
            {
                i += 2; // herestring (<<<), not a heredoc
                continue;
            }

            var declaration = ParseHeredocDeclaration(line, i + 2);
            if (declaration is null)
            {
                i++;
                continue;
            }

            yield return declaration;
            i = declaration.End - 1;
        }
    }

    /// <summary>
    ///     Parses the delimiter of a heredoc introducer starting right after the '&lt;&lt;'. Any
    ///     quoting in the delimiter (&lt;&lt;'EOF', &lt;&lt;"EOF", &lt;&lt;\EOF) marks the body as literal,
    ///     mirroring bash. Returns null when no delimiter token follows the introducer.
    /// </summary>
    private static HeredocDeclaration? ParseHeredocDeclaration(string line, int start)
    {
        var j = start;
        var stripTabs = false;
        if (j < line.Length && line[j] == '-')
        {
            stripTabs = true;
            j++;
        }

        while (j < line.Length && line[j] is ' ' or '\t')
            j++;

        var delimiter = new StringBuilder();
        var quoted = false;
        var quoteChar = '\0';
        var k = j;
        while (k < line.Length)
        {
            var c = line[k];
            if (quoteChar != '\0')
            {
                if (c == quoteChar)
                    quoteChar = '\0';
                else
                    delimiter.Append(c);
                k++;
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quoted = true;
                    quoteChar = c;
                    k++;
                    continue;
                case '\\' when k + 1 < line.Length:
                    quoted = true;
                    delimiter.Append(line[k + 1]);
                    k += 2;
                    continue;
            }

            if (!IsHeredocDelimiterChar(c))
                break;

            delimiter.Append(c);
            k++;
        }

        if (delimiter.Length == 0)
            return null;

        return new HeredocDeclaration(delimiter.ToString(), quoted, stripTabs, k);
    }

    private static bool IsHeredocDelimiterChar(char c)
    {
        return c is not (' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or '(' or ')' or '<' or '>' or '#' or '$' or '`');
    }

    /// <summary>
    ///     True when anything after the heredoc declaration pipes the body into a shell or
    ///     interpreter. Deliberately quote-unaware: on doubt the body is treated as live.
    /// </summary>
    private static bool PipesIntoInterpreter(string line, int start)
    {
        var pipe = line.IndexOf('|', start);
        if (pipe < 0)
            return false;

        var tail = line.AsSpan(pipe + 1);
        foreach (var target in HeredocPipeTargets)
        {
            var offset = 0;
            while (offset <= tail.Length - target.Length)
            {
                var index = tail[offset..].IndexOf(target.AsSpan(), StringComparison.Ordinal);
                if (index < 0)
                    break;

                var absolute = offset + index;
                var before = absolute == 0 ? ' ' : tail[absolute - 1];
                var after = absolute + target.Length >= tail.Length ? ' ' : tail[absolute + target.Length];
                if (!IsWordChar(before) && !IsWordChar(after))
                    return true;

                offset = absolute + 1;
            }
        }

        return false;
    }

    private static bool IsWordChar(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c == '_';
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

    private sealed record Heredoc(string Delimiter, bool StripTabs, bool Suppress);

    private sealed record HeredocDeclaration(string Delimiter, bool Quoted, bool StripTabs, int End);

    private sealed record Rule(SecurityFindingRule Definition, string Pattern, RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}