using System.Text;

namespace Atoll.Api.Services.Security.Scanning;

internal static class ShellHeredocs
{
    // Interpreters that make a quoted-delimiter heredoc body live: piping the body into
    // one of these executes it despite the quoting. Used as a conservative guard - when in
    // doubt the body is scanned as ordinary code.
    private static readonly string[] HeredocPipeTargets =
    [
        "sh", "bash", "zsh", "dash", "ksh", "fish",
        "python", "python2", "python3", "perl", "ruby", "node", "eval"
    ];

    internal static bool IsHeredocSuppressedRule(string ruleId)
    {
        return ruleId == SecurityFindingRules.CommandSubstitution.Id ||
               ruleId == SecurityFindingRules.VariableIndirection.Id;
    }

    /// <summary>
    ///     True when the first non-blank character of a heredoc body line is '#'. Such a line
    ///     is a comment in a live body and documentation text in a data body - either way
    ///     nothing on it is ever executed as a command.
    /// </summary>
    internal static bool IsHeredocCommentLine(string line)
    {
        foreach (var c in line)
        {
            if (c is ' ' or '\t')
                continue;

            return c == '#';
        }

        return false;
    }

    internal static IEnumerable<HeredocDeclaration> ParseHeredocDeclarations(
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
    internal static bool PipesIntoInterpreter(string line, int start)
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

    internal sealed record Heredoc(string Delimiter, bool StripTabs, bool Suppress);

    internal sealed record HeredocDeclaration(string Delimiter, bool Quoted, bool StripTabs, int End);
}
