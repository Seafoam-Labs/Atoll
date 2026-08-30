using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

internal static class NetworkRuleExemptions
{
    /// <summary>
    ///     The connector that feeds the shell in a network rule match: the pipe for
    ///     network-to-shell/decode-to-shell, the connector group for network-execution.
    /// </summary>
    internal static int FindNetworkConnectorIndex(ShellContentScanner.Rule rule, Match match)
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
    internal static bool IsPerlTextFilter(Match match, string normalized)
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
    internal static bool HasShellScriptFileArgument(string normalized, Group shell)
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
}
