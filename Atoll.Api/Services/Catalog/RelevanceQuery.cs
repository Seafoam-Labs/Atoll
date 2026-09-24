using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog;

/// <summary>
///     One free-text relevance term. <see cref="Raw" /> is the trimmed segment, used verbatim for
///     case-sensitive provides lookup and case-insensitive name comparison. <see cref="Posting" /> is
///     the lowercased index key, or <see langword="null" /> when the segment fails the indexing rules
///     and so is unusable for word-posting and infix matching. <see cref="Ordinal" /> is the bit
///     position in the coverage mask.
/// </summary>
internal readonly record struct SearchTerm(string Raw, string? Posting, int Ordinal);

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
            var posting = TokenCleaning.NormalizePosting(segment);

            // De-duplicate on the posting key, falling back to the raw segment when unusable, so
            // "Vim vim" collapses to one term and the coverage mask stays dense.
            if (!seen.Add(posting ?? segment)) continue;

            terms.Add(new SearchTerm(segment, posting, terms.Count));
        }

        return new RelevanceQuery([.. terms]);
    }
}
