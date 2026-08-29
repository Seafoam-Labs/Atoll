using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Security.Scanning;

/// <summary>
///     Determines whether a PKGBUILD can invoke a helper script at build or install time.
///     Mentions in makepkg data arrays and transport commands that stage package content are
///     inert. Every other mention is treated conservatively as reachable code.
/// </summary>
internal static partial class PkgBuildScriptReferences
{
    public static bool IsInvoked(string fileName, string pkgBuildText)
    {
        var dataArrayDepth = 0;

        foreach (var rawLine in pkgBuildText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var positions = ShellSyntax.ComputeQuotePositions(line);
            var dataSpans = GetDataArraySpans(line, positions, ref dataArrayDepth);
            var codeLength = ShellSyntax.StripComment(line).Length;

            for (var search = 0; search < line.Length;)
            {
                var index = line.IndexOf(fileName, search, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;

                search = index + fileName.Length;
                if (IsInertDataMention(index, fileName.Length, dataSpans, positions))
                    continue;

                // A comment mention is intentionally conservative, but an inline comment
                // after a real command must not become that command's apparent destination.
                if (index >= codeLength)
                    return true;

                var command = GetCommandContaining(line[..codeLength], index, positions);
                if (!IsPackageStaging(command))
                    return true;
            }
        }

        return false;
    }

    private static List<(int Start, int End)> GetDataArraySpans(
        string line,
        ShellSyntax.QuotePosition[] positions,
        ref int depth)
    {
        var spans = new List<(int Start, int End)>();
        var cursor = 0;

        while (cursor < line.Length)
        {
            if (depth == 0)
            {
                var assignment = FindDataArrayAssignment(line, positions, cursor);
                if (assignment < 0)
                    break;

                depth = 1;
                cursor = assignment + 1;
            }

            var spanStart = cursor;
            while (cursor < line.Length && depth > 0)
            {
                if (positions[cursor].Region == ShellSyntax.QuoteRegion.Normal)
                {
                    if (line[cursor] == '(')
                        depth++;
                    else if (line[cursor] == ')')
                        depth--;
                }

                cursor++;
            }

            spans.Add((spanStart, depth == 0 ? cursor - 1 : cursor));
        }

        return spans;
    }

    private static int FindDataArrayAssignment(
        string line,
        ShellSyntax.QuotePosition[] positions,
        int start)
    {
        for (var match = ArrayAssignmentRegex().Match(line, start); match.Success; match = match.NextMatch())
        {
            var openParen = match.Index + match.Length - 1;
            if (positions[openParen].Region == ShellSyntax.QuoteRegion.Normal &&
                IsDataArray(match.Groups[1].Value))
                return openParen;
        }

        return -1;
    }

    // makepkg reads these arrays as data. Architecture suffixes (source_x86_64) keep the
    // same treatment; arbitrary shell arrays do not receive this exemption.
    private static bool IsDataArray(string name)
    {
        var stem = name.Split('_')[0];
        return stem is "source" or "noextract" or "validpgpkeys" ||
               stem.EndsWith("sums", StringComparison.Ordinal);
    }

    private static bool IsInertDataMention(
        int start,
        int length,
        IReadOnlyList<(int Start, int End)> spans,
        ShellSyntax.QuotePosition[] positions)
    {
        for (var i = start; i < start + length; i++)
        {
            if (positions[i].Region == ShellSyntax.QuoteRegion.CommandSubstitution ||
                !spans.Any(span => i >= span.Start && i < span.End))
                return false;
        }

        return true;
    }

    private static string GetCommandContaining(
        string line,
        int index,
        ShellSyntax.QuotePosition[] positions)
    {
        var start = index;
        while (start > 0 && !IsCommandSeparator(line, positions, start - 1))
            start--;

        var end = index;
        while (end < line.Length && !IsCommandSeparator(line, positions, end))
            end++;

        return line[start..end].Trim();
    }

    private static bool IsCommandSeparator(
        string line,
        ShellSyntax.QuotePosition[] positions,
        int index)
    {
        return positions[index].Region == ShellSyntax.QuoteRegion.Normal &&
               line[index] is ';' or '&' or '|';
    }

    // Transport commands do not execute their operands. A known package-root destination or
    // a literal relative destination stages content inside the build tree; an absolute or
    // dynamically-computed destination stays conservative.
    private static bool IsPackageStaging(string command)
    {
        if (!TransportCommandRegex().IsMatch(command))
            return false;

        var destination = command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim('"', '\'', ')');
        if (string.IsNullOrEmpty(destination))
            return false;

        if (destination.Contains("pkgdir", StringComparison.OrdinalIgnoreCase) ||
            destination.Contains("destdir", StringComparison.OrdinalIgnoreCase))
            return true;

        return destination[0] is not ('/' or '$' or '`');
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*)\+?=\(", RegexOptions.Compiled)]
    private static partial Regex ArrayAssignmentRegex();

    [GeneratedRegex(@"^(install|cp|mv|ln)\b", RegexOptions.Compiled)]
    private static partial Regex TransportCommandRegex();
}