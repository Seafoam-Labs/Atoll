using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog;

/// <summary>
///     One free-text relevance term. <see cref="Raw" /> is the trimmed segment, used verbatim for
///     case-sensitive provides lookup and case-insensitive name comparison. <see cref="Postings" /> are
///     the lowercased index keys the segment resolves through: the cleaned whole segment first, then
///     its separator- and camelCase-split parts, so a compound segment reaches the interior tiers as
///     well as the name ones. Empty when the indexing rules reject the segment and every part, which
///     leaves the term on the raw-segment tiers only. <see cref="Ordinal" /> is the bit position in the
///     coverage mask, so one segment is one term however many postings it resolves through.
/// </summary>
internal readonly record struct SearchTerm(string Raw, string[] Postings, int Ordinal);

/// <summary>The bounded, normalized term set for one relevance query.</summary>
internal sealed record RelevanceQuery(SearchTerm[] Terms)
{
    public bool IsEmpty => Terms.Length == 0;
}

/// <summary>
///     Parses the raw relevance query into bounded, de-duplicated <see cref="SearchTerm" />s. Does no
///     index access, so bound arithmetic and normalization are testable without a store.
/// </summary>
internal static class RelevanceQueryParser
{
    internal const int MaxQueryLength = 256;
    internal const int MaxTerms = 8;

    /// <summary>
    ///     Postings resolved per term. Six covers 99.4% of corpus names; longer compounds keep their
    ///     leading parts rather than being rejected, because 400ing a pasted name is hostile.
    /// </summary>
    internal const int MaxPostingsPerTerm = 6;

    // Whitespace plus the comma, which the binder uses to join repeated ?query= values.
    private static readonly char[] Separators = [' ', '\t', '\n', '\v', '\f', '\r', ','];

    internal static RelevanceQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new RelevanceQuery([]);

        if (raw.Length > MaxQueryLength)
            throw new ArgumentOutOfRangeException(nameof(raw), raw.Length,
                $"Relevance query must be at most {MaxQueryLength} characters.");

        var segments = raw.Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > MaxTerms)
            throw new ArgumentOutOfRangeException(nameof(raw), segments.Length,
                $"Relevance query must have at most {MaxTerms} terms.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var terms = new List<SearchTerm>(segments.Length);

        foreach (var segment in segments)
        {
            var postings = TokenCleaning.Postings(segment);
            if (postings.Length > MaxPostingsPerTerm) postings = postings[..MaxPostingsPerTerm];

            // De-duplicate on the leading posting, falling back to the raw segment when unusable, so
            // "Vim vim" collapses to one term and the coverage mask stays dense.
            if (!seen.Add(postings.Length > 0 ? postings[0] : segment)) continue;

            terms.Add(new SearchTerm(segment, postings, terms.Count));
        }

        return new RelevanceQuery([.. terms]);
    }
}
