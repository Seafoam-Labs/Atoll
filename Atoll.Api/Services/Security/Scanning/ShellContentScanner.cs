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
        return Scan(content, path, referencedByPkgBuild: true);
    }

    public static IEnumerable<SecurityFinding> Scan(string content, string path, bool referencedByPkgBuild)
    {
        Heredoc? activeHeredoc = null;
        var pendingHeredocs = new Queue<Heredoc>();
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
                foreach (var finding in ScanLine(bodyLine, rawLine, path, isInstallScriptlet, isHelperScript, IsHeredocCommentLine(bodyLine), isUnreferencedScript))
                    if (!activeHeredoc.Suppress || !IsHeredocSuppressedRule(finding.RuleId))
                        yield return finding;

                continue;
            }

            var line = ShellSyntax.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var positions = ShellSyntax.ComputeQuotePositions(line);
            var (arraySpans, endsInsideArray) = GetArrayValueSpans(line, positions, insideArrayValue);
            insideArrayValue = endsInsideArray;
            foreach (var finding in ScanLine(line, positions, rawLine, path, isInstallScriptlet, isHelperScript, isHeredocCommentLine: false, arraySpans, isUnreferencedScript))
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

        var hidden = ShellSyntax.FindHiddenCharacters(line);
        if (hidden.Any(h => !ShellSyntax.IsBenignHiddenCharacter(line, h, positions)))
            yield return Finding(SecurityFindingRules.HiddenCharacter, rawLine, path);
        else if (hidden.Any(h => ShellSyntax.IsZeroWidthCharacter(h.CodePoint)))
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
                if (!IsEvalKeywordInvoked(match, normalized, positions, sourceIndices))
                    continue;

                // A keyword inside an array-assignment value ('depends=(. $(cmd))') is an
                // assigned word, never executed; the substitution keeps its own findings.
                if (IsInertArrayData(match.Groups[2].Index, match.Groups[2].Length, arrayValueSpans, positions, sourceIndices))
                    continue;

                var definition = IsReviewableEval(match, normalized)
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
                if (rule.Definition.Id == SecurityFindingRules.NetworkExecution.Id && IsPerlTextFilter(match, normalized))
                    continue;

                if (rule.Definition.Id == SecurityFindingRules.DecodeToShell.Id &&
                    HasShellScriptFileArgument(normalized, match.Groups[2]))
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
                var index = FindInvokedTool(normalized, tool, positions, sourceIndices, arrayValueSpans);
                if (index < 0)
                    continue;

                if (IsVisibleToolMatch(line, tool, index, positions, sourceIndices))
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
            var index = FindInvokedTool(normalized, tool, positions, sourceIndices, arrayValueSpans);
            if (index < 0)
                continue;

            if (IsVisibleToolMatch(line, tool, index, positions, sourceIndices))
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

    // An array assignment 'name=( … )'. The '=' must sit in unquoted text, so the same
    // text inside a quoted help string never opens a value region.
    private static readonly Regex ArrayAssignmentIntroducer =
        new(@"(?<=^|[;&|)\s])[A-Za-z_][A-Za-z0-9_]*\+?=\(", RegexOptions.Compiled);

    /// <summary>
    ///     Computes the character ranges of this line that lie inside an array-assignment
    ///     value, and whether the line still ends inside one. Unquoted parens are depth-
    ///     tracked so an array can span lines; text inside a command substitution keeps its
    ///     own region and is not part of the inert spans.
    /// </summary>
    private static (List<(int Start, int End)> Spans, bool EndsInside) GetArrayValueSpans(
        string line,
        ShellSyntax.QuotePosition[] positions,
        bool startsInside)
    {
        var spans = new List<(int Start, int End)>();
        var i = 0;
        var inside = startsInside;

        while (i < line.Length)
        {
            if (!inside)
            {
                var introducer = ArrayAssignmentIntroducer.Match(line, i);
                if (!introducer.Success)
                    break;

                var open = introducer.Index + introducer.Length - 1;
                if (positions[open].Region != ShellSyntax.QuoteRegion.Normal)
                {
                    i = open + 1;
                    continue;
                }

                inside = true;
                i = open + 1;
            }

            var depth = 1;
            var close = i;
            while (close < line.Length)
            {
                if (positions[close].Region == ShellSyntax.QuoteRegion.Normal)
                {
                    if (line[close] == '(')
                        depth++;
                    else if (line[close] == ')' && --depth == 0)
                        break;
                }

                close++;
            }

            if (close == line.Length)
            {
                spans.Add((i, line.Length));
                return (spans, true);
            }

            spans.Add((i, close));
            inside = false;
            i = close + 1;
        }

        return (spans, inside);
    }

    /// <summary>
    ///     Returns the first occurrence of the tool that is executed code rather than array
    ///     assignment data, or -1. Words inside a <c>name=( … )</c> value are assigned, never
    ///     invoked, so mentions there (dependency arrays naming sudo or curl) are skipped and
    ///     a later live occurrence on the same line is still found.
    /// </summary>
    private static int FindInvokedTool(
        string normalized,
        string tool,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices,
        IReadOnlyList<(int Start, int End)>? arrayValueSpans)
    {
        var search = 0;
        while (true)
        {
            var index = ShellSyntax.FindUnquotedTool(normalized, tool, search);
            if (index < 0)
                return -1;

            if (!IsInertArrayData(index, tool.Length, arrayValueSpans, positions, sourceIndices))
                return index;

            search = index + tool.Length;
        }
    }

    /// <summary>
    ///     True when the normalized tool match exists verbatim and contiguously in the source.
    ///     Checking this specific match matters: an earlier visible occurrence may be inert
    ///     array data while the selected live occurrence is obfuscated.
    /// </summary>
    private static bool IsVisibleToolMatch(
        string line,
        string tool,
        int normalizedIndex,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        var sourceStart = sourceIndices[normalizedIndex];
        var sourceEnd = sourceIndices[normalizedIndex + tool.Length - 1];
        return sourceEnd - sourceStart == tool.Length - 1 &&
               !ShellSyntax.IsEntirelyInQuotes(positions, sourceIndices, normalizedIndex, tool.Length) &&
               line.AsSpan(sourceStart, tool.Length).Equals(tool, StringComparison.Ordinal);
    }

    /// <summary>
    ///     True when the whole normalized match maps back into an array-assignment value
    ///     outside any command substitution. Assignment words are data; a substitution inside
    ///     an array still executes and keeps its findings.
    /// </summary>
    private static bool IsInertArrayData(
        int normalizedIndex,
        int length,
        IReadOnlyList<(int Start, int End)>? arrayValueSpans,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        if (arrayValueSpans is null)
            return false;

        for (var i = normalizedIndex; i < normalizedIndex + length; i++)
        {
            var source = sourceIndices[i];
            if (positions[source].Region == ShellSyntax.QuoteRegion.CommandSubstitution)
                return false;

            if (!arrayValueSpans.Any(span => source >= span.Start && source < span.End))
                return false;
        }

        return true;
    }

    // Words that can directly precede an invoked command. Any other word before the eval
    // keyword makes it an argument of that command, not an invocation.
    private static readonly HashSet<string> CommandPrefixKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "then", "do", "else", "fi", "done", "esac", "!", "time", "exec", "command",
        "sudo", "doas", "run0", "nohup", "nice", "env"
    };

    // Commands whose output is meant to be eval'd: environment emitters (opam env,
    // makepkg -g, dbus-launch, pifpaf), the classic local-file parsers used to import
    // key=value assignments from PKGBUILDs, Makefiles and /proc, and read-only local
    // hardware monitors (sensors) whose output only feeds further parsing.
    private static readonly HashSet<string> ReviewableEvalCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "opam", "makepkg", "dbus-launch", "pifpaf", "sensors", "grep", "awk", "sed", "cat", "head", "tail"
    };

    /// <summary>
    ///     True when the matched eval/source/./ keyword is a real invocation: unquoted and in
    ///     command position (line start, after a command separator, or after a control
    ///     keyword). A quoted keyword is display text; a keyword after a plain word is an
    ///     argument mention.
    /// </summary>
    private static bool IsEvalKeywordInvoked(
        Match match,
        string normalized,
        ShellSyntax.QuotePosition[] positions,
        int[] sourceIndices)
    {
        var keyword = match.Groups[2];

        var region = positions[sourceIndices[keyword.Index]].Region;
        if (region is ShellSyntax.QuoteRegion.SingleQuoted or ShellSyntax.QuoteRegion.DoubleQuoted)
            return false;

        var j = keyword.Index;
        while (j > 0 && char.IsWhiteSpace(normalized[j - 1]))
            j--;

        if (j == 0)
            return true;

        var previous = normalized[j - 1];
        if (previous is ';' or '&' or '|' or '(' or '`' or '{')
            return true;
        if (previous == ')')
            return false;

        var wordStart = j;
        while (wordStart > 0 && !IsCommandBoundary(normalized[wordStart - 1]))
            wordStart--;

        return CommandPrefixKeywords.Contains(normalized[wordStart..j]);
    }

    private static bool IsCommandBoundary(char c)
    {
        return char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '(' or ')' or '`' or '{' or '}' or '<' or '>';
    }

    /// <summary>
    ///     True when the evaluated text stays reviewable: the command word is fixed
    ///     (eval echo …) with only reviewable substitutions feeding it, or the eval'd output
    ///     comes from a well-known environment emitter, a local file parser, or a local
    ///     path/variable invocation. These are the standard shell idioms; anything else
    ///     (eval $(curl …), eval base64 …) keeps its blocking severity.
    /// </summary>
    private static bool IsReviewableEval(Match match, string normalized)
    {
        var operand = match.Groups[3].Value;
        var argument = TruncateAtCommandEnd(normalized[match.Groups[3].Index..]);

        if (operand is "echo" or "printf")
            return ExtractSubstitutionCommands(argument).All(IsReviewableCommand);

        if (operand is "$(" or "`")
        {
            var commands = ExtractSubstitutionCommands(argument);
            return commands.Count > 0 && IsReviewableCommand(commands[0]);
        }

        return false;
    }

    // Cuts the text at the first command separator that sits outside any command
    // substitution, so later commands on the same line are not part of the eval operand.
    private static string TruncateAtCommandEnd(string text)
    {
        var depth = 0;
        var inBackticks = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                inBackticks = !inBackticks;
                continue;
            }

            if (inBackticks)
                continue;

            switch (c)
            {
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    depth++;
                    i++;
                    break;
                case '(' when depth > 0:
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case ';' or '|' or '&' when depth == 0:
                    return text[..i];
            }
        }

        return text;
    }

    /// <summary>Returns the command text inside every $()/backtick substitution on the line.</summary>
    private static List<string> ExtractSubstitutionCommands(string text)
    {
        var commands = new List<string>();
        int? start = null;
        int? backtickStart = null;
        var depth = 0;
        var inBackticks = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inBackticks)
            {
                if (c == '`')
                {
                    commands.Add(text[backtickStart!.Value..i]);
                    inBackticks = false;
                }

                continue;
            }

            switch (c)
            {
                case '`':
                    inBackticks = true;
                    backtickStart = i + 1;
                    break;
                case '$' when i + 1 < text.Length && text[i + 1] == '(':
                    if (depth == 0)
                        start = i + 2;
                    depth++;
                    i++;
                    break;
                case '(' when depth > 0:
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    if (depth == 0 && start is not null)
                    {
                        commands.Add(text[start.Value..i]);
                        start = null;
                    }

                    break;
            }
        }

        return commands;
    }

    private static bool IsReviewableCommand(string command)
    {
        command = command.Trim();
        if (command.Length == 0)
            return false;

        // A local path or variable invocation: ./helper, /usr/bin/tool, ${dir}/helper, "$BIN".
        if (command[0] is '/' or '.' or '$' or '~' or '"')
            return true;

        var firstToken = command.Split([' ', '\t'], 2)[0];

        // perl only in its configuration-query form; eval $(perl -e '…') runs arbitrary code.
        if (firstToken.Equals("perl", StringComparison.OrdinalIgnoreCase))
        {
            var rest = command[firstToken.Length..].TrimStart();
            return rest.StartsWith("-v", StringComparison.OrdinalIgnoreCase) ||
                   rest.StartsWith("-V", StringComparison.Ordinal);
        }

        return ReviewableEvalCommands.Contains(firstToken);
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
            var position = positions[sourceIndices[FindNetworkConnectorIndex(rule, match)]];
            return position.Escaped ||
                   position.Region is ShellSyntax.QuoteRegion.SingleQuoted or ShellSyntax.QuoteRegion.DoubleQuoted;
        }

        return false;
    }

    /// <summary>
    ///     The connector that feeds the shell in a network rule match: the pipe for
    ///     network-to-shell/decode-to-shell, the connector group for network-execution.
    /// </summary>
    private static int FindNetworkConnectorIndex(Rule rule, Match match)
    {
        if (rule.Definition.Id == SecurityFindingRules.NetworkExecution.Id)
            return match.Groups[3].Index;

        var sourceEnd = rule.Definition.Id == SecurityFindingRules.NetworkToShell.Id
            ? match.Groups[2].Index + match.Groups[2].Length
            : match.Groups[1].Index + match.Groups[1].Length;
        return match.Index + match.Value.IndexOf('|', sourceEnd - match.Index);
    }

    /// <summary>
    ///     True when perl receives its program from inline -e/-E code, so the downloaded
    ///     text arrives on stdin as data for that program (release-tag scraping like
    ///     'curl ... | perl -pe ...') rather than as code to run. A bare perl, a lone '-'
    ///     (read the program from stdin), module flags, or a script file keep the finding.
    /// </summary>
    private static bool IsPerlTextFilter(Match match, string normalized)
    {
        var interpreter = match.Groups[4];
        if (!interpreter.Value.Equals("perl", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var argument in normalized[(interpreter.Index + interpreter.Length)..]
                     .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (argument[0] != '-')
                return false;
            if (argument.Length == 1)
                return false;

            // A letter-only cluster is a bundle of single-character switches: an 'e' in it
            // means the program is the inline code that follows. Other flags (-n, -p,
            // -MModule::Name, -i.bak ...) provide no program source, so keep scanning.
            var cluster = argument[1..];
            if (cluster.All(char.IsAsciiLetter))
            {
                if (cluster.Any(c => c is 'e' or 'E'))
                    return true;
            }
            else if (argument.StartsWith("-e", StringComparison.Ordinal) ||
                     argument.StartsWith("-E", StringComparison.Ordinal))
            {
                // Glued inline code: perl -e'...'
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True when the shell after the pipe reads its script from a file argument, so the
    ///     piped text is only the script's stdin data ('echo yes | bash ./install.sh'
    ///     feeding an answer to an interactive installer). A bare shell word, flag-only
    ///     shells (-s reads commands from stdin) and control operators keep the finding.
    /// </summary>
    private static bool HasShellScriptFileArgument(string normalized, Group shell)
    {
        var rest = normalized[(shell.Index + shell.Length)..].TrimStart();
        var tokenEnd = 0;
        while (tokenEnd < rest.Length && !char.IsWhiteSpace(rest[tokenEnd]) && !IsShellControlChar(rest[tokenEnd]))
            tokenEnd++;

        if (tokenEnd == 0)
            return false;

        return rest[0] is '"' or '\'' or '/' or '.' or '~' || char.IsAsciiLetterOrDigit(rest[0]);
    }

    private static bool IsShellControlChar(char c)
    {
        return c is '|' or ';' or '&' or '(' or ')' or '<' or '>' or '`';
    }

    private static bool IsHeredocSuppressedRule(string ruleId)
    {
        return ruleId == SecurityFindingRules.CommandSubstitution.Id ||
               ruleId == SecurityFindingRules.VariableIndirection.Id;
    }

    /// <summary>
    ///     True when the first non-blank character of a heredoc body line is '#'. Such a line
    ///     is a comment in a live body and documentation text in a data body - either way
    ///     nothing on it is ever executed as a command.
    /// </summary>
    private static bool IsHeredocCommentLine(string line)
    {
        foreach (var c in line)
        {
            if (c is ' ' or '\t')
                continue;

            return c == '#';
        }

        return false;
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
                if (!ShellSyntax.IsWordChar(before) && !ShellSyntax.IsWordChar(after))
                    return true;

                offset = absolute + 1;
            }
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

    private sealed record Heredoc(string Delimiter, bool StripTabs, bool Suppress);

    private sealed record HeredocDeclaration(string Delimiter, bool Quoted, bool StripTabs, int End);

    private sealed record Rule(SecurityFindingRule Definition, string Pattern, RegexOptions Options = RegexOptions.IgnoreCase)
    {
        public Regex Regex { get; } = new(Pattern, Options | RegexOptions.Compiled);
    }
}