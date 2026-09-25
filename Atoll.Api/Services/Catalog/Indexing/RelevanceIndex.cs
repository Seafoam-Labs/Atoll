using System.Collections.Frozen;

namespace Atoll.Api.Services.Catalog.Indexing;

/// <summary>
///     Lookup structures that let ranked retrieval resolve every tier without traversing the name
///     set. Built completely by <see cref="PackageIndexBuilder" /> before the owning
///     <see cref="SearchIndexData" /> is published, so a request never pays construction cost and
///     never observes a half-built generation. Read-only afterwards: the arrays are shared by every
///     request the generation serves.
/// </summary>
public sealed class RelevanceIndex
{
    private RelevanceIndex(
        AurPackageMetadata[] packagesById,
        FrozenDictionary<string, int> idsByName,
        string[] sortedNames,
        int[] sortedIds,
        string[] sortedNameTokens,
        int[] tokenOffsets,
        int[] tokenPostings)
    {
        PackagesById = packagesById;
        IdsByName = idsByName;
        SortedNames = sortedNames;
        SortedIds = sortedIds;
        SortedNameTokens = sortedNameTokens;
        TokenOffsets = tokenOffsets;
        TokenPostings = tokenPostings;
    }

    public static RelevanceIndex Empty { get; } =
        new([], FrozenDictionary<string, int>.Empty, [], [], [], [0], []);

    /// <summary>Package by id. Ids are dense <c>0..Count-1</c> and stable for the generation.</summary>
    internal AurPackageMetadata[] PackagesById { get; }

    /// <summary>Ordinal name to id, for translating the string-keyed provides and word postings.</summary>
    internal FrozenDictionary<string, int> IdsByName { get; }

    /// <summary>
    ///     Names in <see cref="StringComparer.OrdinalIgnoreCase" /> order, parallel to
    ///     <see cref="SortedIds" />. Ordinal-ignore-case order puts every name sharing a prefix (or
    ///     equal to a term) in one contiguous run, which is what turns exact and prefix matching into
    ///     a binary search plus a walk that re-tests with the original predicate.
    /// </summary>
    internal string[] SortedNames { get; }

    internal int[] SortedIds { get; }

    /// <summary>
    ///     Distinct cleaned <em>name</em> tokens in ordinal order. Separate from
    ///     <see cref="SearchIndexData.ByWords" />, which unions name, description, and keyword tokens
    ///     and so cannot attribute a posting to the name.
    /// </summary>
    internal string[] SortedNameTokens { get; }

    /// <summary>CSR start offsets into <see cref="TokenPostings" />; one entry per token plus a terminal.</summary>
    internal int[] TokenOffsets { get; }

    /// <summary>Package ids per token, ascending.</summary>
    internal int[] TokenPostings { get; }

    internal int Count => PackagesById.Length;

    internal static RelevanceIndex Build(
        AurPackageMetadata[] packagesById, string[][] nameTokensById, Dictionary<string, int> idsByName)
    {
        var count = packagesById.Length;

        var sortedIds = new int[count];
        var names = new string[count];
        for (var id = 0; id < count; id++)
        {
            sortedIds[id] = id;
            names[id] = packagesById[id].Name;
        }

        // Ordinal first so case-variant names (which compare equal ignore-case) still sort
        // deterministically across builds.
        Array.Sort(sortedIds, new NameOrder(names));

        var sortedNames = new string[count];
        for (var i = 0; i < count; i++) sortedNames[i] = names[sortedIds[i]];

        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var id = 0; id < count; id++)
            foreach (var token in nameTokensById[id])
            {
                if (!postings.TryGetValue(token, out var posting))
                {
                    posting = new List<int>(2);
                    postings[token] = posting;
                }

                // Ids ascend because packages are visited in id order.
                posting.Add(id);
            }

        var sortedNameTokens = new string[postings.Count];
        postings.Keys.CopyTo(sortedNameTokens, 0);
        Array.Sort(sortedNameTokens, StringComparer.Ordinal);

        var total = 0;
        foreach (var token in sortedNameTokens) total += postings[token].Count;

        var tokenOffsets = new int[sortedNameTokens.Length + 1];
        var tokenPostings = new int[total];
        var cursor = 0;
        for (var i = 0; i < sortedNameTokens.Length; i++)
        {
            tokenOffsets[i] = cursor;
            var posting = postings[sortedNameTokens[i]];
            posting.CopyTo(tokenPostings, cursor);
            cursor += posting.Count;
        }

        tokenOffsets[sortedNameTokens.Length] = cursor;

        return new RelevanceIndex(
            packagesById,
            idsByName.ToFrozenDictionary(StringComparer.Ordinal),
            sortedNames,
            sortedIds,
            sortedNameTokens,
            tokenOffsets,
            tokenPostings);
    }

    private sealed class NameOrder(string[] names) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            var comparison = string.Compare(names[x], names[y], StringComparison.OrdinalIgnoreCase);
            return comparison != 0 ? comparison : string.Compare(names[x], names[y], StringComparison.Ordinal);
        }
    }
}
