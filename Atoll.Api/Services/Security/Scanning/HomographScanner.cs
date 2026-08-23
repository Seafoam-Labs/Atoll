using System.Globalization;
using System.Text;

namespace Atoll.Api.Services.Security.Scanning;

/// <summary>
///     Detects homograph spoofing in PKGBUILD metadata fields: lookalike download hosts,
///     typosquatted dependency names, and invisible characters that make a displayed value
///     differ from the value the shell actually uses. Only the extracted field values are
///     checked - trailing comments and surrounding quotes are stripped first, and free prose
///     (pkgdesc, comments) is deliberately out of scope because it is often legitimately
///     non-ASCII. Check order matches shelly's HomographValidator: hidden/invisible
///     characters, mixed scripts, fullwidth ASCII lookalikes, confusable skeleton.
/// </summary>
internal static class HomographScanner
{
    private enum Script
    {
        Latin,
        Cyrillic,
        Greek,
        Armenian
    }

    // ASCII lookalikes from Cyrillic and Greek, ported from shelly's confusables table.
    // Accented Latin letters are deliberately absent: the attack vector is cross-script
    // spoofing, and legitimate values do use Latin diacritics.
    private static readonly Dictionary<int, char> Confusables = new()
    {
        [0x0430] = 'a', [0x0435] = 'e', [0x043E] = 'o', [0x0440] = 'p',
        [0x0441] = 'c', [0x0445] = 'x', [0x0443] = 'y', [0x0456] = 'i',
        [0x0458] = 'j', [0x0455] = 's', [0x04BB] = 'h', [0x0501] = 'd',
        [0x0410] = 'A', [0x0412] = 'B', [0x0415] = 'E', [0x041A] = 'K',
        [0x041C] = 'M', [0x041D] = 'H', [0x041E] = 'O', [0x0420] = 'P',
        [0x0421] = 'C', [0x0422] = 'T', [0x0425] = 'X',
        [0x03BF] = 'o', [0x03B1] = 'a', [0x03B5] = 'e', [0x03C1] = 'p',
        [0x03BD] = 'v', [0x03B9] = 'i', [0x03BA] = 'k',
        [0x039F] = 'O', [0x0391] = 'A', [0x0392] = 'B', [0x0395] = 'E',
        [0x0397] = 'H', [0x0399] = 'I', [0x039A] = 'K', [0x039C] = 'M',
        [0x039D] = 'N', [0x03A1] = 'P', [0x03A4] = 'T', [0x03A7] = 'X'
    };

    public static IEnumerable<SecurityFinding> Scan(string content, string path)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            // Requirement: check the extracted value only. Comments are stripped
            // quote-aware first so non-ASCII prose after '#' never reaches the checks.
            var line = ShellSyntax.StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            var assignment = MatchAssignment(line);
            if (assignment is null)
                continue;

            var (field, value) = assignment.Value;
            var finding = CheckValue(field, value);
            if (finding is not null)
                yield return new SecurityFinding(
                    SecurityFindingRules.Homograph.Id,
                    SecurityFindingRules.Homograph.Severity,
                    finding,
                    rawLine.Trim(),
                    path);
        }
    }

    private static (string Field, string Value)? MatchAssignment(string line)
    {
        foreach (var field in FieldNames)
        {
            if (!line.StartsWith(field + "=", StringComparison.Ordinal))
                continue;

            var rhs = line[(field.Length + 1)..].TrimStart();
            if (rhs.StartsWith('('))
            {
                // Multi-value assignments are checked element by element, joined so that
                // spoofing spread across several values is still caught.
                var elements = ExtractArrayElements(rhs);
                if (elements.Count == 0)
                    return null;

                return (field, string.Join(" ", elements));
            }

            return (field, ExtractScalar(rhs));
        }

        return null;
    }

    /// <summary>
    ///     Splits an array assignment body into shell words: whitespace outside quotes
    ///     separates elements, quotes are removed, and backslash escapes outside single
    ///     quotes are unescaped - mirroring what the shell would assign.
    /// </summary>
    private static List<string> ExtractArrayElements(string rhs)
    {
        var elements = new List<string>();
        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;

        for (var i = 1; i < rhs.Length; i++)
        {
            var c = rhs[i];
            if (inSingle)
            {
                if (c == '\'')
                    inSingle = false;
                else
                    current.Append(c);
                continue;
            }

            if (inDouble)
            {
                switch (c)
                {
                    case '"':
                        inDouble = false;
                        continue;
                    case '\\' when i + 1 < rhs.Length:
                        current.Append(rhs[i + 1]);
                        i++;
                        continue;
                    default:
                        current.Append(c);
                        continue;
                }
            }

            switch (c)
            {
                case '\'':
                    inSingle = true;
                    continue;
                case '"':
                    inDouble = true;
                    continue;
                case '\\' when i + 1 < rhs.Length:
                    current.Append(rhs[i + 1]);
                    i++;
                    continue;
                case ')':
                    if (current.Length > 0)
                        elements.Add(current.ToString());
                    return elements;
                case ' ' or '\t':
                    if (current.Length > 0)
                    {
                        elements.Add(current.ToString());
                        current.Clear();
                    }

                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        if (current.Length > 0)
            elements.Add(current.ToString());

        return elements;
    }

    private static string ExtractScalar(string rhs)
    {
        var trimmed = rhs.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '\'' && trimmed[^1] == '\'') || (trimmed[0] == '"' && trimmed[^1] == '"')))
            return trimmed[1..^1];

        return trimmed;
    }

    /// <summary>
    ///     Runs the four checks in order and returns the message for the first one that
    ///     fires, or null for a clean value. At most one finding per line, matching shelly.
    /// </summary>
    private static string? CheckValue(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (IsPlainAscii(value))
            return null;

        var invisible = FindInvisibleCodePoint(value);
        if (invisible is not null)
            return Message(value, field,
                $"contains a hidden or invisible character (U+{invisible.Value:X4}) - this can spoof a trusted value or hide content (homograph attack)");

        var mixed = CollectMixedScripts(value);
        if (mixed is not null)
            return Message(value, field,
                $"mixes Latin with {string.Join("/", mixed)} characters - possible homograph spoofing (skeleton '{Skeleton(value)}')");

        if (CodePoints(value).Any(cp => cp is >= 0xFF01 and <= 0xFF5E))
            return Message(value, field,
                "uses fullwidth characters that resemble ASCII - possible homograph spoofing");

        var skeleton = Skeleton(value);
        if (skeleton != value && IsPlainAscii(skeleton))
            return Message(value, field,
                $"contains non-ASCII characters that resemble ASCII (skeleton '{skeleton}') - possible homograph spoofing");

        return null;
    }

    private static string Message(string value, string field, string reason)
    {
        return $"'{Describe(value)}' in {field} {reason}.";
    }

    private static bool IsPlainAscii(string value)
    {
        return value.All(c => c <= 0x7F && !char.IsControl(c));
    }

    /// <summary>
    ///     Finds the first invisible code point in the value. Beyond the classic
    ///     zero-width/bidi/control set this covers format characters and combining marks:
    ///     a mark like U+0670 prepended to a URL is invisible and changes nothing visible,
    ///     yet makes the value differ from what a reviewer sees. The check runs on the
    ///     NFC-normalized value so decomposed accented letters (e + combining acute)
    ///     compose into a single visible letter first and are not mistaken for hidden marks.
    /// </summary>
    private static int? FindInvisibleCodePoint(string value)
    {
        var text = NormalizeFormC(value);
        for (var i = 0; i < text.Length;)
        {
            var codePoint = char.ConvertToUtf32(text, i);
            if (IsInvisibleCodePoint(text, i, codePoint))
                return codePoint;

            i += char.IsSurrogatePair(text, i) ? 2 : 1;
        }

        return null;
    }

    private static bool IsInvisibleCodePoint(string text, int index, int codePoint)
    {
        if (codePoint is not '\t' and not '\n' and not '\r' &&
            (codePoint < 0x20 || codePoint is >= 0x7F and <= 0x9F))
            return true;

        return char.GetUnicodeCategory(text, index) is UnicodeCategory.Format
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
    }

    private static string NormalizeFormC(string value)
    {
        try
        {
            return value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 (unpaired surrogates); scan the value as-is.
            return value;
        }
    }

    /// <summary>
    ///     The names of the scripts mixed with Latin, when at least one of the
    ///     ASCII-lookalike-prone scripts (Cyrillic, Greek, Armenian) is present. Other
    ///     scripts - CJK, Hangul, Arabic, ... - are ignored: they cannot spoof ASCII and
    ///     legitimate values (internationalized domain names, localized filenames) use them.
    /// </summary>
    private static IReadOnlyList<Script>? CollectMixedScripts(string value)
    {
        var hasLatin = false;
        List<Script>? mixed = null;

        foreach (var codePoint in CodePoints(value))
        {
            var script = Classify(codePoint);
            if (script is null)
                continue;

            if (script == Script.Latin)
            {
                hasLatin = true;
            }
            else
            {
                mixed ??= [];
                if (!mixed.Contains(script.Value))
                    mixed.Add(script.Value);
            }
        }

        return hasLatin && mixed is { Count: > 0 } ? mixed : null;
    }

    private static Script? Classify(int codePoint)
    {
        if (codePoint is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 0x00C0 and <= 0x024F)
            return Script.Latin;
        if (codePoint is >= 0x0370 and <= 0x03FF)
            return Script.Greek;
        if (codePoint is >= 0x0400 and <= 0x04FF)
            return Script.Cyrillic;
        if (codePoint is >= 0x0530 and <= 0x058F)
            return Script.Armenian;

        return null;
    }

    private static string Skeleton(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var codePoint in CodePoints(value))
            builder.Append(Confusables.TryGetValue(codePoint, out var ascii) ? ascii : char.ConvertFromUtf32(codePoint));

        return builder.ToString();
    }

    /// <summary>Renders a value for messages, replacing non-ASCII with [U+XXXX] escapes.</summary>
    private static string Describe(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var codePoint in CodePoints(value))
            builder.Append(codePoint <= 0x7F && !char.IsControl((char)codePoint)
                ? (char)codePoint
                : $"[U+{codePoint:X4}]");

        return builder.ToString();
    }

    private static IEnumerable<int> CodePoints(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                yield return text[i];
            }
        }
    }

    private static readonly string[] FieldNames = ["pkgname", "depends", "makedepends", "url", "source"];
}
