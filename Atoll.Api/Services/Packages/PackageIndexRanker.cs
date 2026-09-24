using System.Collections.Immutable;
using System.ComponentModel;
using Atoll.Api.Services.Caching;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Packages.Persistence;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages;

/// <summary>
/// Ranks the seeded-package set (Mongo <c>packages</c> names) by in-memory catalog keys. The name
/// list and one sorted name array per sort key are entries in the shared cache, so a sorted page
/// costs O(limit) instead of sorting the whole corpus. Everything carries the <c>catalog</c> tag:
/// seeded-set writes drop it, the worker's warm re-stores all of it after every refresh cycle, and
/// the configured TTL is the backstop for the cycles that fail.
/// </summary>
internal sealed class PackageIndexRanker(
    IPackageRepository repo,
    HybridCache cache,
    PackageIndexStore indexStore,
    IOptions<AtollOptions> options)
{
    private readonly HybridCacheEntryOptions _rankOptions = new()
    {
        Expiration = TimeSpan.FromSeconds(options.Value.Caching.RankTtlSeconds),
        LocalCacheExpiration = TimeSpan.FromSeconds(options.Value.Caching.RankTtlSeconds),
    };

    // Every (sort, order) the ranked REST path serves; `name asc` pages MongoDB directly instead.
    private static readonly (PackageIndexSortBy SortBy, PackageIndexSortOrder Order)[] ServedSorts =
    [
        (PackageIndexSortBy.Name, PackageIndexSortOrder.Desc),
        (PackageIndexSortBy.Votes, PackageIndexSortOrder.Asc),
        (PackageIndexSortBy.Votes, PackageIndexSortOrder.Desc),
        (PackageIndexSortBy.Popularity, PackageIndexSortOrder.Asc),
        (PackageIndexSortBy.Popularity, PackageIndexSortOrder.Desc),
        (PackageIndexSortBy.Version, PackageIndexSortOrder.Asc),
        (PackageIndexSortBy.Version, PackageIndexSortOrder.Desc)
    ];

    // Shared by the read and warm paths so the two stores of one entry cannot drift apart on tags.
    private static readonly string[] RankTags = [AtollCacheKeys.TagCatalog];

    private SearchIndexData CurrentIndex => indexStore.Current;

    /// <summary>
    /// Sorted names for one sort key. The returned array is shared; callers must not mutate it.
    /// </summary>
    public async Task<string[]> GetSortedNamesAsync(
        PackageIndexSortBy sortBy,
        PackageIndexSortOrder order,
        CancellationToken ct)
    {
        var names = (await GetNamesAsync(ct)).Names;

        // Captured when the array is ranked; a later index swap is only seen once the worker's warm
        // rebuilds the entry, or a write drops the tag.
        var catalog = CurrentIndex.ByNames;
        var sorted = (await cache.GetOrCreateAsync(
            AtollCacheKeys.RankSorted(sortBy, order),
            (names, catalog, sortBy, order),
            static (state, _) => new ValueTask<RankedNames>(
                BuildRanked(state.names, state.catalog, state.sortBy, state.order)),
            _rankOptions,
            RankTags,
            ct)).Names;

        return sorted;
    }

    /// <summary>
    ///     Rebuilds every served sorted view from the current index generation, and re-stores the
    ///     MongoDB-backed name list to re-arm its TTL: a read hit does not extend an absolute
    ///     expiration, so a fill-only warm would let the repository scan fall back onto a request.
    ///     Re-arming is safe here because every <c>catalog</c> writer changes the name set.
    /// </summary>
    internal async Task PrewarmAsync(CancellationToken ct)
    {
        var ranked = await GetNamesAsync(ct);
        await cache.SetAsync(AtollCacheKeys.RankNames, ranked, _rankOptions, RankTags, ct);

        // One generation for the whole warm, so the arrays cannot disagree across a mid-warm swap.
        var catalog = CurrentIndex.ByNames;
        foreach (var (sortBy, order) in ServedSorts)
            await cache.SetAsync(
                AtollCacheKeys.RankSorted(sortBy, order),
                BuildRanked(ranked.Names, catalog, sortBy, order),
                _rankOptions,
                RankTags,
                ct);
    }

    private ValueTask<RankedNames> GetNamesAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(
            AtollCacheKeys.RankNames,
            FetchNamesAsync,
            _rankOptions,
            RankTags,
            ct);

    private async ValueTask<RankedNames> FetchNamesAsync(CancellationToken ct)
    {
        var names = await repo.ListAsync(ct);
        return new RankedNames([.. names]);
    }

    private static RankedNames BuildRanked(
        string[] names,
        ImmutableDictionary<string, AurPackageMetadata> catalog,
        PackageIndexSortBy sortBy,
        PackageIndexSortOrder order) =>
        new(Rank(names, catalog, sortBy, order));

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
