using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Indexing;

/// <summary>
///     Splits raw tokens into unique, lowercased, index-ready terms.
///     Pipeline: separator split → camelCase split → length filter
///     → ASCII filter → leading-digits filter → pure-numeric filter
///     → stop-word filter → deduplication.
/// </summary>
/// <remarks>
///     Revisit: ASCII-only filter drops accented chars; leading-digits threshold
///     of 2 means "30fps" is skipped but "3d" passes; <see cref="AllowedShortTerms" />
///     is hand-maintained and should move to config if it grows.
/// </remarks>
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

    /// <summary>
    ///     Short identifiers below <see cref="MinimumTokenLength" /> that are
    ///     meaningful for search.
    /// </summary>
    private static readonly HashSet<string> AllowedShortTerms = ["i3", "xz", "7z"];

    /// <summary>
    ///     Splits on punctuation/symbols/brackets.
    ///     <example>"foo-bar.baz" → "foo", "bar", "baz"</example>
    /// </summary>
    [GeneratedRegex("[-_!,:/()\\[\\].'+?=*\"#$%&{}|;~\\\\<>@`^]")]
    private static partial Regex SeparatorsRegex();

    /// <summary>
    ///     Splits at lower→upper or digit→upper boundaries.
    ///     <example>"XmlHttpRequest" → "Xml", "Http", "Request"</example>
    /// </summary>
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

    /// <summary>U+0020–U+007F only; drop if internationalized content is needed.</summary>
    private static bool IsPrintableAscii(char c)
    {
        return (int)c is >= 32 and <= 127;
    }

    /// <summary>"30fps" skipped, "3d" passes. Lower the threshold if needed.</summary>
    private static bool StartsWithTwoDigits(string s)
    {
        return s.Length > 1 && char.IsAsciiDigit(s[0]) && char.IsAsciiDigit(s[1]);
    }
}