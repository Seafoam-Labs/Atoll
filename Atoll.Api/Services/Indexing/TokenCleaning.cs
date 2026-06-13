using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Indexing;

public static partial class TokenCleaning
{
    private static readonly HashSet<string> IgnoredTerms =
    [
        "for", "and", "the", "with", "from", "that", "your", "git", "bin", "this", "not", "svn",
        "who", "can", "you", "like", "into", "all", "more", "one", "any", "over", "non", "them",
        "are", "very", "when", "about", "yet", "many", "its", "also", "most", "lets", "just"
    ];

    [GeneratedRegex("[-_!,:/()\\[\\].'+?=*\"#$%&{}|;~\\\\<>@`^]")]
    private static partial Regex SeparatorsRegex();

    public static IEnumerable<string> SplitAndClean(IEnumerable<string> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in source)
        foreach (var split in SeparatorsRegex().Split(token))
        {
            if (split.Length == 0) continue;

            if (split.Length <= 2 && split != "i3") continue;

            if (!split.All(IsPrintableAscii)) continue;

            if (StartsWithTwoDigits(split)) continue;

            var lowered = split.ToLowerInvariant();
            if (IgnoredTerms.Contains(lowered)) continue;

            if (seen.Add(lowered)) yield return lowered;
        }
    }

    private static bool IsPrintableAscii(char c)
    {
        var n = (int)c;
        return n is >= 32 and <= 127;
    }

    private static bool IsNumeric(char c)
    {
        var n = (int)c;
        return n is >= 48 and <= 57;
    }

    private static bool StartsWithTwoDigits(string s)
    {
        return s.Length > 1 && IsNumeric(s[0]) && IsNumeric(s[1]);
    }
}