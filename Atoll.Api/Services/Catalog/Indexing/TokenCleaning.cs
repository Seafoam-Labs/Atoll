using System.Text.RegularExpressions;

namespace Atoll.Api.Services.Catalog.Indexing;

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
    [GeneratedRegex("[-_!,:/()\\[\\].'+?=*\"#$%&{}|;~\\\\<>@`^]", RegexOptions.None, 250)]
    private static partial Regex SeparatorsRegex { get; }

    /// <summary>
    ///     Splits at lower→upper or digit→upper boundaries.
    ///     <example>"XmlHttpRequest" → "Xml", "Http", "Request"</example>
    /// </summary>
    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.None, 250)]
    private static partial Regex CamelCaseRegex { get; }

    public static IEnumerable<string> SplitAndClean(IEnumerable<string> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in source)
        foreach (var split in SeparatorsRegex.Split(token))
        foreach (var part in CamelCaseRegex.Split(split))
        {
            var lowered = NormalizePosting(part);
            if (lowered is not null && seen.Add(lowered)) yield return lowered;
        }
    }

    /// <summary>
    ///     Applies the indexing filters to one already-split segment and returns the lowercased
    ///     posting key, or <see langword="null" /> when the segment is rejected (too short and not
    ///     an allowed short term, non-ASCII, leading two digits, all digits, or a stop word). Shared
    ///     by <see cref="SplitAndClean" /> and the relevance parser so both sides clean identically.
    /// </summary>
    internal static string? NormalizePosting(string segment)
    {
        var lowered = segment.ToLowerInvariant();

        if (segment.Length < MinimumTokenLength && !AllowedShortTerms.Contains(lowered)) return null;
        if (!segment.All(IsPrintableAscii)) return null;
        if (StartsWithTwoDigits(segment)) return null;
        if (segment.All(char.IsAsciiDigit)) return null;
        if (IgnoredTerms.Contains(lowered)) return null;

        return lowered;
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