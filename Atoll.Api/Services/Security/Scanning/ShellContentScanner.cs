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

    public static IEnumerable<SecurityFinding> Scan(string content, string path, bool referencedByPkgBuild = true)
    {
        ShellHeredocs.Heredoc? activeHeredoc = null;
        var pendingHeredocs = new Queue<ShellHeredocs.Heredoc>();
        var insideArrayValue = false;
        var isInstallScriptlet = PackageBuildFileClassifier.IsInstallScriptlet(path);
        var isHelperScript = PackageBuildFileClassifier.IsHelperScript(path);
        var isUnreferencedScript = !referencedByPkgBuild && !isInstallScriptlet;

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
                foreach (var finding in ScanLine(bodyLine, rawLine, path, isInstallScriptlet, isHelperScript, ShellHeredocs.IsHeredocCommentLine(bodyLine), isUnreferencedScript))
                    if (!activeHeredoc.Suppress || !ShellHeredocs.IsHeredocSuppressedRule(finding.RuleId))
                        yield return finding;

                continue;
            }

            var line = ShellSyntax.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var positions = ShellSyntax.ComputeQuotePositions(line);
            var (arraySpans, endsInsideArray) = ShellArraySpans.GetArrayValueSpans(line, positions, insideArrayValue);
            insideArrayValue = endsInsideArray;
            foreach (var finding in ScanLine(line, positions, rawLine, path, isInstallScriptlet, isHelperScript, isHeredocCommentLine: false, arraySpans, isUnreferencedScript))
                yield return finding;

            foreach (var declaration in ShellHeredocs.ParseHeredocDeclarations(line, positions))
            {
                var suppress = declaration.Quoted && !ShellHeredocs.PipesIntoInterpreter(line, declaration.End);
                var heredoc = new ShellHeredocs.Heredoc(declaration.Delimiter, declaration.StripTabs, suppress);
                if (activeHeredoc is null)
                    activeHeredoc = heredoc;
                else
                    pendingHeredocs.Enqueue(heredoc);
            }
        }
    }

    private static IEnumerable<SecurityFinding> ScanLine(
        string line,
        string rawLine,
        string path,
        bool isInstallScriptlet,
        bool isHelperScript,
        bool isHeredocCommentLine,
        bool isUnreferencedScript)
    {
        return ScanLine(line, ShellSyntax.ComputeQuotePositions(line), rawLine, path, isInstallScriptlet, isHelperScript, isHeredocCommentLine, arrayValueSpans: null, isUnreferencedScript);
    }

    private static IEnumerable<SecurityFinding> ScanLine(
        string line,
        ShellSyntax.QuotePosition[] positions,
        string rawLine,
        string path,
        bool isInstallScriptlet,
        bool isHelperScript,
        bool isHeredocCommentLine,
        IReadOnlyList<(int Start, int End)>? arrayValueSpans,
        bool isUnreferencedScript = false)
    {
        if (line.Length == 0)
            yield break;

        var hidden = HiddenCharacters.FindHiddenCharacters(line);
        if (hidden.Any(h => !HiddenCharacters.IsBenignHiddenCharacter(line, h, positions)))
            yield return Finding(SecurityFindingRules.HiddenCharacter, rawLine, path);
        else if (hidden.Any(h => HiddenCharacters.IsZeroWidthCharacter(h.CodePoint)))
            yield return Finding(SecurityFindingRules.HiddenCharacterZeroWidth, rawLine, path);

        var (normalized, sourceIndices) = ShellSyntax.NormalizeForMatching(line);
        foreach (var rule in Rules)
        {
            var match = rule.Regex.Match(normalized);
            if (!match.Success)
                continue;

            if (IsInertQuotedMatch(rule, match, positions, sourceIndices))
                continue;

            if (rule.Definition.Id == SecurityFindingRules.EvalIndirection.Id)
            {
                // 'eval'/'source'/'.' must be an actual invocation in command position;
                // display text and argument mentions ("open source EchoLink") are inert.
                if (!ShellEvalClassifier.IsEvalKeywordInvoked(match, normalized, positions, sourceIndices))
                    continue;

                // A keyword inside an array-assignment value ('depends=(. $(cmd))') is an
                // assigned word, never executed; the substitution keeps its own findings.
                if (ShellArraySpans.IsInertArrayData(match.Groups[2].Index, match.Groups[2].Length, arrayValueSpans, positions, sourceIndices))
                    continue;

                var definition = ShellEvalClassifier.IsReviewableEval(match, normalized)
                    ? SecurityFindingRules.EvalIndirectionComputed
                    : rule.Definition;
                yield return Finding(definition, rawLine, path);
                continue;
            }

            if (rule.Definition.Id == SecurityFindingRules.WriteOutsideBuildRoot.Id)
            {
                // A '#' line in a heredoc body is a comment or documentation text - a
                // redirect on it can never run, in a live body as much as in a data body.
                if (isHeredocCommentLine)
                    continue;

                var obfuscated = !rule.Regex.IsMatch(line) &&
                                 !ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, match.Index, match.Length);

                // Scriptlets run as root under alpm's control: writing system files from
                // one is its ordinary job, so the finding is kept for review but does not
                // block - obfuscated constructs included, since the context, not the
                // syntax, is what makes the write benign.
                if (isInstallScriptlet)
                {
                    var scriptletRule = SecurityFindingRules.WriteOutsideBuildRootScriptlet;
                    yield return Finding(scriptletRule, rawLine, path,
                        message: obfuscated
                            ? scriptletRule.Description + " The construct was also obfuscated, but scriptlets run as root under alpm's control either way."
                            : null);
                    continue;
                }

                // The PKGBUILD never references this file, so nothing in it runs during
                // build or install: the write stays visible for review but cannot execute
                // as part of packaging. An obfuscated construct still signals hidden
                // intent and escalates below instead of taking the downgrade.
                if (isUnreferencedScript && !obfuscated)
                {
                    yield return Finding(SecurityFindingRules.WriteOutsideBuildRootUnreferenced, rawLine, path);
                    continue;
                }

                yield return Finding(rule.Definition, rawLine, path,
                    obfuscated ? FindingSeverity.Critical : null,
                    obfuscated
                        ? rule.Definition.Description + " The construct was also obfuscated, which is a strong sign of malicious intent."
                        : null);
                continue;
            }

            // Structural exemptions for the pipe rules: these prove the fetched or decoded
            // text is data rather than executed code, so the rule's premise does not hold.
            // Obfuscated matches skip the exemptions and keep their escalation below.
            if (rule.Regex.IsMatch(line))
            {
                if (rule.Definition.Id == SecurityFindingRules.NetworkExecution.Id && NetworkRuleExemptions.IsPerlTextFilter(match, normalized))
                    continue;

                if (rule.Definition.Id == SecurityFindingRules.DecodeToShell.Id &&
                    NetworkRuleExemptions.HasShellScriptFileArgument(normalized, match.Groups[2]))
                    continue;
            }

            var obfuscatedMatch = !rule.Regex.IsMatch(line) &&
                                  !ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, match.Index, match.Length);
            yield return Finding(rule.Definition, rawLine, path,
                obfuscatedMatch ? FindingSeverity.Critical : null,
                obfuscatedMatch
                    ? rule.Definition.Description + " The construct was also obfuscated, which is a strong sign of malicious intent."
                    : null);
        }

        // '#' lines in heredoc bodies are comments or documentation text: no privilege
        // tool on one can ever run, obfuscated or not.
        if (!isHeredocCommentLine)
            foreach (var tool in PrivilegeEscalationTools)
            {
                var index = ShellArraySpans.FindInvokedTool(normalized, tool, positions, sourceIndices, arrayValueSpans);
                if (index < 0)
                    continue;

                if (ShellArraySpans.IsVisibleToolMatch(line, tool, index, positions, sourceIndices))
                {
                    // Scriptlets already run as root under alpm's control, so an escalation
                    // tool inside one is redundant rather than an escalation. Helper scripts
                    // ship in the package and only run when the user invokes them voluntarily,
                    // typically as root, so the tool grants nothing the user did not grant.
                    SecurityFindingRule rule;
                    if (isInstallScriptlet)
                        rule = SecurityFindingRules.PrivilegeEscalationScriptlet;
                    else if (isHelperScript)
                        rule = SecurityFindingRules.PrivilegeEscalationHelperScript;
                    else
                        rule = SecurityFindingRules.PrivilegeEscalation;
                    yield return Finding(rule, rawLine, path, message: string.Format(rule.Description, tool));
                    continue;
                }

                // Only visible after de-obfuscation. A match that maps entirely inside quoted
                // regions of the original line is an escape-stripping artifact (display text
                // like "\$(sudo ...)" where the backslash prevents execution), not hidden
                // intent: suppress it instead of escalating.
                if (ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, index, tool.Length))
                    continue;

                if (isInstallScriptlet)
                {
                    var scriptletRule = SecurityFindingRules.PrivilegeEscalationScriptlet;
                    yield return Finding(scriptletRule, rawLine, path,
                        message: string.Format(scriptletRule.Description, tool) +
                                 " The tool name was also obfuscated, but scriptlets run as root under alpm's control either way.");
                    continue;
                }

                if (isHelperScript)
                {
                    var helperRule = SecurityFindingRules.PrivilegeEscalationHelperScript;
                    yield return Finding(helperRule, rawLine, path,
                        message: string.Format(helperRule.Description, tool) +
                                 " The tool name was also obfuscated, but helper scripts only run when the user invokes them voluntarily either way.");
                    continue;
                }

                yield return Finding(SecurityFindingRules.PrivilegeEscalation, rawLine, path, FindingSeverity.Critical,
                    $"Privilege escalation tool '{tool}' is invoked via obfuscated shell syntax - the tool name was deliberately hidden, which is a strong sign of malicious intent.");
            }

        foreach (var tool in RiskyTools)
        {
            var index = ShellArraySpans.FindInvokedTool(normalized, tool, positions, sourceIndices, arrayValueSpans);
            if (index < 0)
                continue;

            if (ShellArraySpans.IsVisibleToolMatch(line, tool, index, positions, sourceIndices))
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

        if (ruleId == SecurityFindingRules.NetworkToShell.Id ||
            ruleId == SecurityFindingRules.DecodeToShell.Id ||
            ruleId == SecurityFindingRules.NetworkExecution.Id)
        {
            // The construct only runs when the connector actually pipes: a '|' inside quoted
            // display text (help and usage strings, optdepends notes) is printed, not fed to
            // a shell. A pipe inside a command substitution keeps its own region and stays
            // live even when the substitution itself is quoted.
            var position = positions[sourceIndices[NetworkRuleExemptions.FindNetworkConnectorIndex(rule, match)]];
            return position.Escaped ||
                   position.Region is ShellSyntax.QuoteRegion.SingleQuoted or ShellSyntax.QuoteRegion.DoubleQuoted;
        }

        return false;
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

    internal sealed record Rule(SecurityFindingRule Definition, string Pattern, RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}
