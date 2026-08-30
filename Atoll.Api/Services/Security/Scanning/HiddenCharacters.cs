namespace Atoll.Api.Services.Security.Scanning;

internal static class HiddenCharacters
{
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
    public static bool IsBenignHiddenCharacter(string line, HiddenCharacter character, ShellSyntax.QuotePosition[] positions)
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

        return positions[character.Index].Region is ShellSyntax.QuoteRegion.SingleQuoted or ShellSyntax.QuoteRegion.DoubleQuoted;
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

    private static bool IsHiddenCodePoint(int codePoint)
    {
        if (codePoint is 0x200B or 0x200C or 0x200D or 0xFEFF or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
            return true;

        return codePoint != '\t' && codePoint != '\n' && codePoint != '\r' &&
               codePoint is < 0x20 or >= 0x7F and <= 0x9F;
    }
}
