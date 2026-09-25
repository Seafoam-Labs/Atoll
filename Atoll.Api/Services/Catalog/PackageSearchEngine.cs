using System.Runtime.InteropServices;
using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog;

/// <summary>
/// Generation-scoped search primitives shared by the REST, Blazor, and RPC adapters. Callers
/// capture once per operation so a single request only ever sees one index generation.
/// </summary>
public sealed class PackageSearchEngine(PackageIndexStore store)
{
    // Inverted ranking order, so a PriorityQueue built on it keeps the worst surviving hit at the top.
    private static readonly Comparer<PackageSearchHit> WorstFirst =
        Comparer<PackageSearchHit>.Create(static (x, y) => CompareHits(y, x));

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
    ///     Ranked relevance retrieval. Parses the raw query, resolves every tier through the
    ///     generation's <see cref="RelevanceIndex" /> lookups rather than traversing the name set,
    ///     keeps the best tier each candidate earned per term, and returns the ranking. Tier and score
    ///     stay internal; the adapter projects the packages and applies the response cap. A
    ///     <paramref name="limit" /> keeps only the best N, byte-identical to the first N of the
    ///     unlimited ranking because <see cref="CompareHits" /> is a total order. Static because the
    ///     internal return type bars the public-instance shape the other primitives use.
    /// </summary>
    internal static PackageSearchHit[] Rank(SearchIndexData snapshot, string rawQuery, int? limit = null)
    {
        var query = RelevanceQueryParser.Parse(rawQuery);
        if (query.IsEmpty) return [];

        var index = snapshot.Relevance;
        if (index.Count == 0) return [];

        var states = new Dictionary<int, CandidateState>();

        foreach (var term in query.Terms)
        {
            RecordProvidesMatches(snapshot, index, term, states);
            RecordNameMatches(index, term, states);
            RecordWordMatches(snapshot, index, term, states);
            RecordNameTokenMatches(index, term, states);
        }

        return limit is { } cap ? SelectTop(states, cap) : SelectAll(states);
    }

    private static void RecordProvidesMatches(
        SearchIndexData snapshot, RelevanceIndex index, SearchTerm term, Dictionary<int, CandidateState> states)
    {
        if (!snapshot.ByProvides.TryGetValue(term.Raw, out var names)) return;

        foreach (var name in names)
            if (index.IdsByName.TryGetValue(name, out var id))
                Record(states, index, id, term.Ordinal, SearchTier.ExactProvides);
    }

    private static void RecordWordMatches(
        SearchIndexData snapshot, RelevanceIndex index, SearchTerm term, Dictionary<int, CandidateState> states)
    {
        foreach (var posting in term.Postings)
        {
            if (!snapshot.ByWords.TryGetValue(posting, out var names)) continue;

            foreach (var name in names)
                if (index.IdsByName.TryGetValue(name, out var id))
                    Record(states, index, id, term.Ordinal, SearchTier.WordPosting);
        }
    }

    /// <summary>
    ///     Exact-name and name-prefix hits in one walk. Both occupy the same contiguous run of the
    ///     ignore-case-sorted names, so a single lower bound locates the run and the original
    ///     predicate decides which of the two tiers each name earned. Sorting and membership testing
    ///     use the same comparison, so the run is exactly the set the predicate accepts.
    /// </summary>
    private static void RecordNameMatches(RelevanceIndex index, SearchTerm term, Dictionary<int, CandidateState> states)
    {
        var names = index.SortedNames;

        for (var i = LowerBound(names, term.Raw, StringComparer.OrdinalIgnoreCase);
             i < names.Length && names[i].StartsWith(term.Raw, StringComparison.OrdinalIgnoreCase);
             i++)
        {
            var tier = names[i].Equals(term.Raw, StringComparison.OrdinalIgnoreCase)
                ? SearchTier.ExactName
                : SearchTier.NamePrefix;

            Record(states, index, index.SortedIds[i], term.Ordinal, tier);
        }
    }

    /// <summary>
    ///     Exact-name-token and token-prefix hits, walked once per key the term resolves through: the
    ///     lowered raw segment plus every posting that is not already that segment. The raw one is
    ///     walked even when the pipeline rejects it as a posting (too short, a stop word), which is
    ///     what keeps short queries on the name tiers. Vocabulary entries are already lowercased by
    ///     the indexing pipeline, so the keys meet them as they are.
    /// </summary>
    private static void RecordNameTokenMatches(
        RelevanceIndex index, SearchTerm term, Dictionary<int, CandidateState> states)
    {
        var loweredRaw = term.Raw.ToLowerInvariant();
        RecordNameTokenMatches(index, loweredRaw, term.Ordinal, states);

        foreach (var posting in term.Postings)
            if (!posting.Equals(loweredRaw, StringComparison.Ordinal))
                RecordNameTokenMatches(index, posting, term.Ordinal, states);
    }

    /// <summary>
    ///     One vocabulary walk. Exact and prefix hits for a key occupy the same contiguous run of the
    ///     ordinal-sorted vocabulary, so a single lower bound locates the run and the original
    ///     predicate decides which of the two tiers each token earned. Sorting and membership testing
    ///     use the same comparison, so the run is exactly the set the predicate accepts.
    /// </summary>
    private static void RecordNameTokenMatches(
        RelevanceIndex index, string key, int ordinal, Dictionary<int, CandidateState> states)
    {
        var tokens = index.SortedNameTokens;

        for (var i = LowerBound(tokens, key, StringComparer.Ordinal);
             i < tokens.Length && tokens[i].StartsWith(key, StringComparison.Ordinal);
             i++)
        {
            var tier = tokens[i].Equals(key, StringComparison.Ordinal)
                ? SearchTier.NameToken
                : SearchTier.NameTokenPrefix;

            for (var posting = index.TokenOffsets[i]; posting < index.TokenOffsets[i + 1]; posting++)
                Record(states, index, index.TokenPostings[posting], ordinal, tier);
        }
    }

    private static void Record(
        Dictionary<int, CandidateState> states, RelevanceIndex index, int id, int ordinal, SearchTier tier)
    {
        ref var state = ref CollectionsMarshal.GetValueRefOrAddDefault(states, id, out var exists);
        if (!exists) state.Package = index.PackagesById[id];
        state.Record(ordinal, tier);
    }

    private static int LowerBound(string[] sorted, string target, StringComparer comparer)
    {
        var low = 0;
        var high = sorted.Length;

        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (comparer.Compare(sorted[middle], target) < 0) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    private static PackageSearchHit[] SelectAll(Dictionary<int, CandidateState> states)
    {
        var hits = new PackageSearchHit[states.Count];
        var filled = 0;
        foreach (var state in states.Values) hits[filled++] = state.ToHit();

        Array.Sort(hits, CompareHits);
        return hits;
    }

    /// <summary>
    ///     Bounded top-N: the queue holds the best <paramref name="limit" /> hits seen so far with the
    ///     worst of them at the top, so a better arrival replaces that one in a single shift-down
    ///     instead of re-sorting the candidate set.
    /// </summary>
    private static PackageSearchHit[] SelectTop(Dictionary<int, CandidateState> states, int limit)
    {
        if (limit <= 0) return [];
        if (states.Count <= limit) return SelectAll(states);

        var best = new PriorityQueue<PackageSearchHit, PackageSearchHit>(limit, WorstFirst);

        foreach (var state in states.Values)
        {
            var hit = state.ToHit();
            if (best.Count < limit) best.Enqueue(hit, hit);
            else if (CompareHits(hit, best.Peek()) < 0) best.EnqueueDequeue(hit, hit);
        }

        // Dequeue yields the worst of the survivors first, so fill from the back.
        var top = new PackageSearchHit[limit];
        for (var i = limit - 1; i >= 0; i--) top[i] = best.Dequeue();

        return top;
    }

    // Total order: the chain ends in the ordinal package name, which is unique across ByNames. If that
    // final tiebreak is ever dropped, Array.Sort and the PriorityQueue are both unstable and relevance
    // order becomes nondeterministic.
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

    /// <summary>
    ///     Per-candidate accumulator: the best tier earned for each term packed three bits at a time
    ///     into one word (tier + 1, so 0 means unmatched and no initialization pass is needed),
    ///     reduced to one hit. The packing holds ten terms; the parser caps queries at
    ///     <see cref="RelevanceQueryParser.MaxTerms" />, so the word never overflows.
    /// </summary>
    private struct CandidateState
    {
        private const uint SlotMask = 0b111;
        private const int BitsPerTerm = 3;

        public AurPackageMetadata Package;

        private uint _tiers;

        public void Record(int ordinal, SearchTier tier)
        {
            var shift = ordinal * BitsPerTerm;
            var incoming = (uint)tier + 1;
            var current = (_tiers >> shift) & SlotMask;

            if (current == 0 || incoming < current)
                _tiers = (_tiers & ~(SlotMask << shift)) | (incoming << shift);
        }

        public PackageSearchHit ToHit()
        {
            var matched = 0;
            var sum = 0;
            var best = int.MaxValue;

            for (var shift = 0; shift < sizeof(uint) * 8; shift += BitsPerTerm)
            {
                var slot = (_tiers >> shift) & SlotMask;
                if (slot == 0) continue;

                var tier = (int)slot - 1;
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
    WordPosting
}

/// <summary>A ranked relevance result. Tier and score are internal; only <see cref="Package" /> is served.</summary>
internal readonly record struct PackageSearchHit(
    AurPackageMetadata Package, SearchTier Tier, int MatchedTermCount, int TierSum);
