using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

internal static class ShellEvalClassifier
{
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
    internal static bool IsEvalKeywordInvoked(
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
    internal static bool IsReviewableEval(Match match, string normalized)
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
}
