using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog;

/// <summary>
/// Generation-scoped search primitives shared by the REST, Blazor, and RPC adapters. Callers
/// capture once per operation so a single request only ever sees one index generation.
/// </summary>
public sealed class PackageSearchEngine(PackageIndexStore store)
{
    private static readonly char[] NameSeparators = ['-', '_'];

    /// <summary>The only store read; every primitive below takes the captured snapshot.</summary>
    public SearchIndexData Capture() => store.Current;

    public AurPackageMetadata? FindByName(SearchIndexData snapshot, string name) =>
        snapshot.ByNames.GetValueOrDefault(name);

    /// <summary>Hydrates in caller order, skipping unknown names; dedupe is the caller's set's job.</summary>
    public AurPackageMetadata[] Hydrate(SearchIndexData snapshot, IEnumerable<string> names) =>
        [
            .. names
                .Select(name => snapshot.ByNames.GetValueOrDefault(name))
                .Where(package => package is not null)
                .Cast<AurPackageMetadata>()
        ];

    /// <summary>
    /// Union of exact provides postings, accumulated in query-key order. Set enumeration order is
    /// what the legacy response orders inherit, so the construction order is part of the contract.
    /// </summary>
    public IReadOnlySet<string> MatchProvides(SearchIndexData snapshot, IReadOnlySet<string> names)
    {
        var matchingNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (snapshot.ByProvides.TryGetValue(name, out var packageNames))
                matchingNames.UnionWith(packageNames);
        }

        return matchingNames;
    }

    /// <summary>
    /// AND-intersection of word postings; null when the term set is empty or any term is missing.
    /// The first matched posting is copied before intersecting, so the result's enumeration order
    /// derives from that posting rather than from set internals.
    /// </summary>
    public IReadOnlySet<string>? MatchWords(SearchIndexData snapshot, IReadOnlySet<string> words)
    {
        if (words.Count == 0) return null;

        HashSet<string>? intersection = null;

        foreach (var word in words)
        {
            if (!snapshot.ByWords.TryGetValue(word, out var packageNames)) return null;

            if (intersection is null)
            {
                intersection = [.. packageNames];
                continue;
            }

            intersection.IntersectWith(packageNames);
            if (intersection.Count == 0) return null;
        }

        return intersection;
    }

    /// <summary>Enumeration escape hatch for protocol adapters (RPC search/suggest, catalog sorted views).</summary>
    public IEnumerable<AurPackageMetadata> All(SearchIndexData snapshot) => snapshot.ByNames.Values;

    /// <summary>
    ///     Ranked relevance retrieval. Parses the raw query, collects candidates from exactly three
    ///     sources (a linear name scan, per-term word postings, per-term provides postings), keeps the
    ///     best tier each candidate earned per term, and returns everything sorted. Tier and score stay
    ///     internal; the adapter projects the packages and applies the response cap. Static because the
    ///     internal return type bars the public-instance shape the other primitives use.
    /// </summary>
    internal static PackageSearchHit[] Rank(SearchIndexData snapshot, string rawQuery)
    {
        var query = RelevanceQueryParser.Parse(rawQuery);
        if (query.IsEmpty) return [];

        var termCount = query.Terms.Length;
        var lowerTerms = new string[termCount];
        foreach (var term in query.Terms) lowerTerms[term.Ordinal] = term.Raw.ToLowerInvariant();

        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        RecordProvidesMatches(snapshot, query, candidates);
        RecordNameMatches(snapshot, query, lowerTerms, candidates);
        RecordWordMatches(snapshot, query, candidates);

        var hits = new List<PackageSearchHit>(candidates.Count);
        foreach (var candidate in candidates.Values) hits.Add(candidate.ToHit());

        hits.Sort(CompareHits);
        return [.. hits];
    }

    private static void RecordProvidesMatches(
        SearchIndexData snapshot, RelevanceQuery query, Dictionary<string, Candidate> candidates)
    {
        foreach (var term in query.Terms)
        {
            if (!snapshot.ByProvides.TryGetValue(term.Raw, out var names)) continue;

            foreach (var name in names)
            {
                var package = snapshot.ByNames.GetValueOrDefault(name);
                if (package is null) continue;
                GetOrAdd(candidates, name, package, query.Terms.Length).Record(term.Ordinal, SearchTier.ExactProvides);
            }
        }
    }

    private static void RecordWordMatches(
        SearchIndexData snapshot, RelevanceQuery query, Dictionary<string, Candidate> candidates)
    {
        foreach (var term in query.Terms)
        {
            if (term.Posting is null) continue;
            if (!snapshot.ByWords.TryGetValue(term.Posting, out var names)) continue;

            foreach (var name in names)
            {
                var package = snapshot.ByNames.GetValueOrDefault(name);
                if (package is null) continue;
                GetOrAdd(candidates, name, package, query.Terms.Length).Record(term.Ordinal, SearchTier.WordPosting);
            }
        }
    }

    private static void RecordNameMatches(
        SearchIndexData snapshot, RelevanceQuery query, string[] lowerTerms, Dictionary<string, Candidate> candidates)
    {
        foreach (var (name, package) in snapshot.ByNames)
        {
            string[]? tokens = null;

            foreach (var term in query.Terms)
            {
                var tier = ClassifyName(name, term, lowerTerms[term.Ordinal], ref tokens);
                if (tier is not null)
                    GetOrAdd(candidates, name, package, query.Terms.Length).Record(term.Ordinal, tier.Value);
            }
        }
    }

    /// <summary>
    ///     Best name-based tier for one (name, term) pair, or null when the name does not match. The
    ///     Contains prefilter gates the tokenizer: every name-side tier implies the name contains the
    ///     term, so a single traversal of <c>ByNames</c> tokenizes only the handful of names that
    ///     survive. <paramref name="tokens" /> is materialized once per name and reused across its terms.
    /// </summary>
    private static SearchTier? ClassifyName(string name, SearchTerm term, string lowerTerm, ref string[]? tokens)
    {
        if (name.Equals(term.Raw, StringComparison.OrdinalIgnoreCase)) return SearchTier.ExactName;
        if (name.StartsWith(term.Raw, StringComparison.OrdinalIgnoreCase)) return SearchTier.NamePrefix;
        if (!name.Contains(term.Raw, StringComparison.OrdinalIgnoreCase)) return null;

        tokens ??= Tokenize(name);

        foreach (var token in tokens)
            if (token.Equals(lowerTerm, StringComparison.Ordinal)) return SearchTier.NameToken;

        foreach (var token in tokens)
            if (token.StartsWith(lowerTerm, StringComparison.Ordinal)) return SearchTier.NameTokenPrefix;

        return term.Posting is not null ? SearchTier.NameInfix : null;
    }

    private static string[] Tokenize(string name) =>
        [.. TokenCleaning.SplitAndClean(name.Split(NameSeparators, StringSplitOptions.None))];

    private static Candidate GetOrAdd(
        Dictionary<string, Candidate> candidates, string name, AurPackageMetadata package, int termCount)
    {
        if (candidates.TryGetValue(name, out var candidate)) return candidate;

        candidate = new Candidate(package, termCount);
        candidates[name] = candidate;
        return candidate;
    }

    // Total order: the chain ends in the ordinal package name, which is unique across ByNames. If that
    // final tiebreak is ever dropped, List<T>.Sort is unstable and relevance order becomes nondeterministic.
    private static int CompareHits(PackageSearchHit x, PackageSearchHit y)
    {
        var comparison = y.MatchedTermCount.CompareTo(x.MatchedTermCount);
        if (comparison != 0) return comparison;

        comparison = ((int)x.Tier).CompareTo((int)y.Tier);
        if (comparison != 0) return comparison;

        comparison = x.TierSum.CompareTo(y.TierSum);
        if (comparison != 0) return comparison;

        comparison = y.Package.NumVotes.CompareTo(x.Package.NumVotes);
        if (comparison != 0) return comparison;

        return string.CompareOrdinal(x.Package.Name, y.Package.Name);
    }

    /// <summary>Per-candidate accumulator: the best tier earned for each term, reduced to one hit.</summary>
    private sealed class Candidate
    {
        private readonly int[] _tierByTerm;

        public Candidate(AurPackageMetadata package, int termCount)
        {
            Package = package;
            _tierByTerm = new int[termCount];
            Array.Fill(_tierByTerm, -1);
        }

        public AurPackageMetadata Package { get; }

        public void Record(int ordinal, SearchTier tier)
        {
            var incoming = (int)tier;
            if (_tierByTerm[ordinal] < 0 || incoming < _tierByTerm[ordinal]) _tierByTerm[ordinal] = incoming;
        }

        public PackageSearchHit ToHit()
        {
            var matched = 0;
            var sum = 0;
            var best = int.MaxValue;

            foreach (var tier in _tierByTerm)
            {
                if (tier < 0) continue;
                matched++;
                sum += tier;
                if (tier < best) best = tier;
            }

            return new PackageSearchHit(Package, (SearchTier)best, matched, sum);
        }
    }
}

/// <summary>Relevance match quality, best (lowest) first. Never leaves the server.</summary>
internal enum SearchTier
{
    ExactName = 0,
    ExactProvides,
    NamePrefix,
    NameToken,
    NameTokenPrefix,
    WordPosting,
    NameInfix
}

/// <summary>A ranked relevance result. Tier and score are internal; only <see cref="Package" /> is served.</summary>
internal readonly record struct PackageSearchHit(
    AurPackageMetadata Package, SearchTier Tier, int MatchedTermCount, int TierSum);
