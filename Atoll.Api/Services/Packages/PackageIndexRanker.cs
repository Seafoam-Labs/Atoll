using System.Collections.Immutable;
using System.ComponentModel;
using Atoll.Api.Services.Caching;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Packages.Persistence;
using Microsoft.Extensions.Caching.Hybrid;

namespace Atoll.Api.Services.Packages;

/// <summary>
/// Ranks the seeded-package set (Mongo <c>packages</c> names) by in-memory catalog keys. The name
/// list and one sorted name array per sort key are entries in the shared cache, so a sorted page
/// costs O(limit) instead of sorting the whole corpus. Everything carries the <c>catalog</c> tag:
/// seeded-set writes drop it, and the 30 s TTL is the backstop.
/// </summary>
internal sealed class PackageIndexRanker(
    IPackageRepository repo,
    HybridCache cache,
    PackageIndexStore indexStore)
{
    private static readonly HybridCacheEntryOptions RankOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
    };

    private SearchIndexData CurrentIndex => indexStore.Current;

    /// <summary>
    /// Sorted names for one sort key. The returned array is shared; callers must not mutate it.
    /// </summary>
    public async Task<string[]> GetSortedNamesAsync(
        PackageIndexSortBy sortBy,
        PackageIndexSortOrder order,
        CancellationToken ct)
    {
        var names = (await cache.GetOrCreateAsync(
            AtollCacheKeys.RankNames,
            FetchNamesAsync,
            RankOptions,
            [AtollCacheKeys.TagCatalog],
            ct)).Names;

        // Captured when the array is ranked; an index swap after that is not seen until the entry
        // TTL expires or a write drops the tag.
        var catalog = CurrentIndex.ByNames;
        var sorted = (await cache.GetOrCreateAsync(
            AtollCacheKeys.RankSorted(sortBy, order),
            (names, catalog, sortBy, order),
            static (state, _) => new ValueTask<RankedNames>(
                new RankedNames(Rank(state.names, state.catalog, state.sortBy, state.order))),
            RankOptions,
            [AtollCacheKeys.TagCatalog],
            ct)).Names;

        return sorted;
    }

    private async ValueTask<RankedNames> FetchNamesAsync(CancellationToken ct)
    {
        var names = await repo.ListAsync(ct);
        return new RankedNames([.. names]);
    }

    private static string[] Rank(
        string[] names,
        ImmutableDictionary<string, AurPackageMetadata> catalog,
        PackageIndexSortBy sortBy,
        PackageIndexSortOrder order)
    {
        var descending = order is PackageIndexSortOrder.Desc;

        return sortBy switch
        {
            PackageIndexSortBy.Name => SortNames(names, descending),
            PackageIndexSortBy.Votes => SortByKey(names, catalog, static m => m.NumVotes, 0L,
                Comparer<long>.Default, descending),
            PackageIndexSortBy.Popularity => SortByKey(names, catalog, static m => m.Popularity, 0d,
                Comparer<double>.Default, descending),
            PackageIndexSortBy.Version => SortByKey(names, catalog, static m => m.Version, null,
                StringComparer.Ordinal, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, null)
        };
    }

    // Decorate once per name, then sort plain key fields: catalog lookups inside the comparator would
    // cost two lookups per comparison, a few million for a full corpus instead of one per name.
    private static string[] SortByKey<TKey>(
        string[] names,
        ImmutableDictionary<string, AurPackageMetadata> catalog,
        Func<AurPackageMetadata, TKey> keySelector,
        TKey absentKey,
        IComparer<TKey> keyComparer,
        bool descending)
    {
        var decorated = new (TKey Key, string Name)[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            decorated[i] = (catalog.TryGetValue(name, out var metadata) ? keySelector(metadata) : absentKey, name);
        }

        Array.Sort(decorated, (a, b) =>
        {
            // Negating the ascending comparison keeps nulls last on descending (StringComparer.Ordinal
            // puts null first ascending). The name tie-break stays ascending in both directions.
            var comparison = keyComparer.Compare(a.Key, b.Key);
            if (descending) comparison = -comparison;
            return comparison != 0 ? comparison : string.CompareOrdinal(a.Name, b.Name);
        });

        var sorted = new string[decorated.Length];
        for (var i = 0; i < decorated.Length; i++)
            sorted[i] = decorated[i].Name;
        return sorted;
    }

    private static string[] SortNames(string[] names, bool descending)
    {
        var sorted = (string[])names.Clone();
        Array.Sort(sorted, descending
            ? static (a, b) => string.CompareOrdinal(b, a)
            : static (a, b) => string.CompareOrdinal(a, b));
        return sorted;
    }

    // ImmutableObject marks the payload safe to share by reference, so warm L1 reads skip the
    // serialization clone unmarked records pay.
    [ImmutableObject(true)]
    private sealed record RankedNames(string[] Names);
}
