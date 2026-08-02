using System.Text;

namespace Atoll.Api.Services.Security.Scanning;

internal static class ShellSyntax
{
    public static string StripComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var i = 0; i < line.Length; i++)
            switch (line[i])
            {
                case '"' when !inSingleQuote:
                    inDoubleQuote = !inDoubleQuote;
                    break;
                case '\'' when !inDoubleQuote:
                    inSingleQuote = !inSingleQuote;
                    break;
                case '#' when !inSingleQuote && !inDoubleQuote:
                    return line[..i];
            }

        return line;
    }

    public static string NormalizeForMatching(string line)
    {
        var normalized = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            switch (c)
            {
                case '\\' when i + 1 < line.Length && line[i + 1] != '\\' && !char.IsWhiteSpace(line[i + 1]):
                    continue;
                case '\'' or '"' when i + 1 < line.Length && line[i + 1] == c:
                    i++;
                    continue;
                default:
                    normalized.Append(c);
                    break;
            }
        }

        return normalized.ToString();
    }

    public static bool ContainsHiddenCharacter(string line)
    {
        for (var i = 0; i < line.Length;)
        {
            var codePoint = char.ConvertToUtf32(line, i);
            if (IsHiddenCodePoint(codePoint))
                return true;

            i += char.IsSurrogatePair(line, i) ? 2 : 1;
        }

        return false;
    }

    public static bool MatchesUnquotedTool(string text, string tool)
    {
        var quotedMask = ComputeQuotedMask(text);
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(tool, start, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (!IsEntirelyQuoted(index, tool.Length, quotedMask) &&
                (index == 0 || IsShellBoundary(text[index - 1])) &&
                (index + tool.Length == text.Length || char.IsWhiteSpace(text[index + tool.Length])))
                return true;

            start = index + 1;
        }
    }

    private static bool[] ComputeQuotedMask(string text)
    {
        var mask = new bool[text.Length];
        var stack = new Stack<QuoteRegion>();
        var current = QuoteRegion.Normal;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (current)
            {
                case QuoteRegion.Normal:
                    switch (c)
                    {
                        case '\'':
                            stack.Push(current);
                            current = QuoteRegion.SingleQuoted;
                            break;
                        case '"':
                            stack.Push(current);
                            current = QuoteRegion.DoubleQuoted;
                            break;
                        case '\\' when i + 1 < text.Length:
                            i++;
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            break;
                    }

                    break;
                case QuoteRegion.SingleQuoted:
                    if (c == '\'') current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                    else mask[i] = true;
                    break;
                case QuoteRegion.DoubleQuoted:
                    switch (c)
                    {
                        case '"':
                            current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                            break;
                        case '\\' when i + 1 < text.Length:
                            mask[i + 1] = true;
                            i++;
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            break;
                        default:
                            mask[i] = true;
                            break;
                    }

                    break;
                case QuoteRegion.CommandSubstitution:
                    switch (c)
                    {
                        case '\'':
                            stack.Push(current);
                            current = QuoteRegion.SingleQuoted;
                            break;
                        case '"':
                            stack.Push(current);
                            current = QuoteRegion.DoubleQuoted;
                            break;
                        case '\\' when i + 1 < text.Length:
                            i++;
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            break;
                        case '(':
                            stack.Push(current);
                            break;
                        case ')':
                            current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                            break;
                    }

                    break;
                default:
                    throw new InvalidOperationException($"Unexpected quote region: {current}.");
            }
        }

        return mask;
    }

    private static bool IsEntirelyQuoted(int index, int length, bool[] quotedMask)
    {
        for (var i = index; i < index + length; i++)
            if (!quotedMask[i])
                return false;
        return true;
    }

    private static bool IsShellBoundary(char c)
    {
        return c is ' ' or '\t' or '\n' or '\r' or (char)0x0B or (char)0x0C or ';' or '&' or '|' or '`' or '(';
    }

    private static bool IsHiddenCodePoint(int codePoint)
    {
        if (codePoint is 0x200B or 0x200C or 0x200D or 0xFEFF or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
            return true;

        return codePoint != '\t' && codePoint != '\n' && codePoint != '\r' &&
               codePoint is < 0x20 or >= 0x7F and <= 0x9F;
    }

    private enum QuoteRegion
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        CommandSubstitution
    }
}