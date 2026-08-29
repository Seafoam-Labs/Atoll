using System.Text;

namespace Atoll.Api.Services.Security.Scanning;

internal static class ShellSyntax
{
    public enum QuoteRegion
    {
        Normal,
        SingleQuoted,
        DoubleQuoted,
        CommandSubstitution
    }

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

    /// <summary>
    ///     De-obfuscates a line for matching. <c>SourceIndices[i]</c> is the position in the
    ///     original line that each surviving normalized character came from, so matches on the
    ///     normalized text can be mapped back onto the original quote structure.
    /// </summary>
    public static (string Text, int[] SourceIndices) NormalizeForMatching(string line)
    {
        var normalized = new StringBuilder(line.Length);
        var sourceIndices = new int[line.Length];
        var count = 0;
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
                // A quote with word characters on both sides is always removed by shell
                // quote removal (c'u'rl -> curl), so dropping it reveals quote-split tool
                // names. Edge quotes ('npm', "curl") are kept: they turn the word into a
                // single quoted string, i.e. display text, not an invocation.
                case '\'' or '"' when i > 0 && i + 1 < line.Length && IsWordChar(line[i - 1]) && IsWordChar(line[i + 1]):
                    continue;
                default:
                    normalized.Append(c);
                    sourceIndices[count++] = i;
                    break;
            }
        }

        return (normalized.ToString(), sourceIndices[..count]);
    }

    public static bool IsWordChar(char c)
    {
        return char.IsAsciiLetterOrDigit(c) || c == '_';
    }

    /// <summary>
    ///     Tracks the quote region at every position of a line. The region reported for a
    ///     character is the one in effect when the shell reads it: an opening quote is still in
    ///     the outer region, its content is in the new one.
    /// </summary>
    public static QuotePosition[] ComputeQuotePositions(string text)
    {
        var positions = new QuotePosition[text.Length];
        var stack = new Stack<QuoteRegion>();
        var current = QuoteRegion.Normal;

        for (var i = 0; i < text.Length; i++)
        {
            positions[i] = new QuotePosition(current, false);
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
                            positions[i] = new QuotePosition(current, true);
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            positions[i] = new QuotePosition(current, false);
                            break;
                    }

                    break;
                case QuoteRegion.SingleQuoted:
                    if (c == '\'') current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                    break;
                case QuoteRegion.DoubleQuoted:
                    switch (c)
                    {
                        case '"':
                            current = stack.Count > 0 ? stack.Pop() : QuoteRegion.Normal;
                            break;
                        case '\\' when i + 1 < text.Length:
                            i++;
                            positions[i] = new QuotePosition(current, true);
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            positions[i] = new QuotePosition(current, false);
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
                            positions[i] = new QuotePosition(current, true);
                            break;
                        case '$' when i + 1 < text.Length && text[i + 1] == '(':
                            stack.Push(current);
                            current = QuoteRegion.CommandSubstitution;
                            i++;
                            positions[i] = new QuotePosition(current, false);
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

        return positions;
    }

    /// <summary>
    ///     True when every character of a normalized-text span maps back into a single- or
    ///     double-quoted region of the original line. A match that only exists there because
    ///     de-obfuscation stripped load-bearing escapes is not evidence of hidden intent.
    /// </summary>
    public static bool IsEntirelyInQuotes(QuotePosition[] positions, int[] sourceIndices, int normalizedStart, int length)
    {
        for (var i = normalizedStart; i < normalizedStart + length; i++)
        {
            var region = positions[sourceIndices[i]].Region;
            if (region is not (QuoteRegion.SingleQuoted or QuoteRegion.DoubleQuoted))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Enumerates suspicious invisible code points on the line. Complete ANSI escape
    ///     sequences (ESC [ … final byte) are skipped: terminal styling changes how output
    ///     looks, never how the shell parses the line.
    /// </summary>
    public static List<HiddenCharacter> FindHiddenCharacters(string line)
    {
        var found = new List<HiddenCharacter>();
        for (var i = 0; i < line.Length;)
        {
            if (line[i] == '\x1b')
            {
                var end = FindAnsiSequenceEnd(line, i);
                if (end > i)
                {
                    i = end;
                    continue;
                }
            }

            var codePoint = char.ConvertToUtf32(line, i);
            if (IsHiddenCodePoint(codePoint))
                found.Add(new HiddenCharacter(i, codePoint));

            i += char.IsSurrogatePair(line, i) ? 2 : 1;
        }

        return found;
    }

    /// <summary>
    ///     True when a suspicious code point is benign in its context. Zero-width characters
    ///     are inert in any position (the shell never treats them as separators), control
    ///     bytes inside quoted strings are display data, and C1 bytes next to Latin-1
    ///     supplement characters are mojibake (double-encoded UTF-8 file names), not
    ///     deliberate obfuscation. Bidi overrides never qualify - they can reorder displayed
    ///     text, so the reviewed code can differ from what runs.
    /// </summary>
    public static bool IsBenignHiddenCharacter(string line, HiddenCharacter character, QuotePosition[] positions)
    {
        var codePoint = character.CodePoint;
        if (codePoint is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
            return false;

        if (IsZeroWidthCharacter(codePoint))
            return true;

        // A bare escape that is not part of a complete CSI sequence can still drive
        // terminal-injection sequences (OSC and friends), even inside quotes.
        if (codePoint == 0x1B)
            return false;

        if (codePoint is >= 0x80 and <= 0x9F && IsInsideMojibakeRun(line, character.Index))
            return true;

        return positions[character.Index].Region is QuoteRegion.SingleQuoted or QuoteRegion.DoubleQuoted;
    }

    // Returns the index after a complete CSI sequence starting at the ESC byte, or -1 when
    // the sequence is unterminated or not CSI at all.
    private static int FindAnsiSequenceEnd(string line, int start)
    {
        if (start + 1 >= line.Length || line[start + 1] != '[')
            return -1;

        for (var i = start + 2; i < line.Length; i++)
        {
            var c = line[i];
            if (c is >= '\x20' and <= '\x3F')
                continue;
            if (c is >= '\x40' and <= '\x7E')
                return i + 1;
            return -1;
        }

        return -1;
    }

    private static bool IsInsideMojibakeRun(string line, int index)
    {
        return index > 0 && line[index - 1] is >= '\x80' and <= '\xFF' ||
               index + 1 < line.Length && line[index + 1] is >= '\x80' and <= '\xFF';
    }

    /// <summary>
    ///     Zero-width characters (ZWSP, ZWNJ, ZWJ, BOM). They are never shell separators, so
    ///     they cannot make executed code differ from reviewed code - a review concern only.
    /// </summary>
    public static bool IsZeroWidthCharacter(int codePoint)
    {
        return codePoint is 0x200B or 0x200C or 0x200D or 0xFEFF;
    }

    /// <summary>An invisible code point on the line and the index of its first UTF-16 unit.</summary>
    public readonly record struct HiddenCharacter(int Index, int CodePoint);

    public static bool MatchesUnquotedTool(string text, string tool)
    {
        return FindUnquotedTool(text, tool) >= 0;
    }

    /// <summary>Returns the index of the first invoked (unquoted, boundary-delimited) occurrence of the tool at or after <paramref name="start"/>, or -1.</summary>
    public static int FindUnquotedTool(string text, string tool, int start = 0)
    {
        var quotedMask = ComputeQuotedMask(text);
        while (true)
        {
            var index = text.IndexOf(tool, start, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            if (!IsEntirelyQuoted(index, tool.Length, quotedMask) &&
                (index == 0 || IsShellBoundary(text[index - 1])) &&
                (index + tool.Length == text.Length || char.IsWhiteSpace(text[index + tool.Length])))
                return index;

            start = index + 1;
        }
    }

    private static bool[] ComputeQuotedMask(string text)
    {
        var positions = ComputeQuotePositions(text);
        var mask = new bool[text.Length];
        for (var i = 0; i < text.Length; i++)
            mask[i] = positions[i].Region switch
            {
                QuoteRegion.SingleQuoted => text[i] != '\'',
                QuoteRegion.DoubleQuoted => text[i] != '"',
                _ => false
            };

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

    /// <summary>The quote region in effect at a character, and whether a backslash escape made it inert.</summary>
    public readonly record struct QuotePosition(QuoteRegion Region, bool Escaped);
}