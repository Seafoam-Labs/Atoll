using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Indexing;

public static partial class TokenCleaning
{
    private const int MinimumTokenLength = 3;

    private static readonly HashSet<string> IgnoredTerms =
    [
        // Articles, conjunctions, prepositions
        "for", "and", "the", "with", "from", "that", "this", "not", "into", "all",
        "but", "out", "how", "each", "than", "too", "now", "off", "per",
        // Pronouns
        "your", "who", "you", "them", "are", "his", "her", "our", "my", "their",
        "she", "him", "me", "we", "us", "its",
        // Low-value verbs & adverbs
        "can", "like", "more", "one", "any", "over", "non", "very", "when", "about",
        "yet", "many", "also", "most", "lets", "just", "has", "had", "was", "did",
        "get", "got", "use", "using", "used", "make", "made", "run", "set", "put",
        "try", "see", "say", "add", "new", "own", "way", "will", "may",
        // Indexing noise
        "git", "svn", "bin", "www", "com", "org", "net", "http", "https", "html",
        "php", "css", "xml", "json", "sql", "tmp", "log", "err", "var", "etc",
        "api", "url", "src", "lib", "cfg", "dir", "env"
    ];

    private static readonly HashSet<string> AllowedShortTerms = ["i3", "xz", "7z"];

    [GeneratedRegex("[-_!,:/()\\[\\].'+?=*\"#$%&{}|;~\\\\<>@`^]")]
    private static partial Regex SeparatorsRegex();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseRegex();

    public static IEnumerable<string> SplitAndClean(IEnumerable<string> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in source)
        foreach (var split in SeparatorsRegex().Split(token))
        foreach (var part in CamelCaseRegex().Split(split))
        {
            if (part.Length < MinimumTokenLength
                && !AllowedShortTerms.Contains(part.ToLowerInvariant())) continue;

            if (!part.All(IsPrintableAscii)) continue;

            if (StartsWithTwoDigits(part)) continue;

            if (part.All(char.IsAsciiDigit)) continue;

            var lowered = part.ToLowerInvariant();
            if (IgnoredTerms.Contains(lowered)) continue;

            if (seen.Add(lowered)) yield return lowered;
        }
    }

    private static bool IsPrintableAscii(char c)
    {
        return (int)c is >= 32 and <= 127;
    }

    private static bool StartsWithTwoDigits(string s)
    {
        return s.Length > 1 && char.IsAsciiDigit(s[0]) && char.IsAsciiDigit(s[1]);
    }
}