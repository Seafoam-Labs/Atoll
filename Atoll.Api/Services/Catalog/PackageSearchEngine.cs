using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Catalog;

/// <summary>
/// Generation-scoped search primitives shared by the REST, Blazor, and RPC adapters. Callers
/// capture once per operation so a single request only ever sees one index generation.
/// </summary>
public sealed class PackageSearchEngine(PackageIndexStore store)
{
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
}
