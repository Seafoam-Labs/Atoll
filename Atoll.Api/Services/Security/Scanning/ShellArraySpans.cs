using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

internal static partial class ShellArraySpans
{
    // An array assignment 'name=( … )'. The '=' must sit in unquoted text, so the same
    // text inside a quoted help string never opens a value region.
    [GeneratedRegex(@"(?<=^|[;&|)\s])[A-Za-z_][A-Za-z0-9_]*\+?=\(", RegexOptions.Compiled)]
    private static partial Regex ArrayAssignmentIntroducer();

    /// <summary>
    ///     Computes the character ranges of this line that lie inside an array-assignment
    ///     value, and whether the line still ends inside one. Unquoted parens are depth-
    ///     tracked so an array can span lines; text inside a command substitution keeps its
    ///     own region and is not part of the inert spans.
    /// </summary>
    internal static (List<(int Start, int End)> Spans, bool EndsInside) GetArrayValueSpans(
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
                var introducer = ArrayAssignmentIntroducer().Match(line, i);
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
    internal static int FindInvokedTool(
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
    internal static bool IsVisibleToolMatch(
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
    internal static bool IsInertArrayData(
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
}