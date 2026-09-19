using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Packages;

/// <summary>
/// Ranks the seeded-package set (Mongo <c>packages</c> names) by in-memory catalog keys and caches one
/// sorted name array per sort key, so a sorted page costs O(limit) instead of sorting the whole corpus.
/// </summary>
internal sealed class PackageIndexRanker(IPackageRepository repo, PackageIndexStore? indexStore)
{
    private static readonly TimeSpan GenerationTtl = TimeSpan.FromSeconds(30);

    // Keyed on the index instance so PackageIndexStore.Replace drops a generation's views with the
    // index it was built from. Nothing stored here may reference the key, or the weak key keeps
    // itself alive.
    private readonly ConditionalWeakTable<SearchIndexData, RankCache> _caches = new();

    private SearchIndexData CurrentIndex => indexStore?.Current ?? SearchIndexData.Empty;

    /// <summary>
    /// Sorted names for one sort key. The returned array is shared; callers must not mutate it.
    /// </summary>
    public async Task<string[]> GetSortedNamesAsync(
        PackageIndexSortBy sortBy,
        PackageIndexSortOrder order,
        CancellationToken ct)
    {
        var index = CurrentIndex;
        var generation = await GetGenerationAsync(_caches.GetValue(index, static _ => new RankCache()), ct);
        var catalog = index.ByNames;

        // Lazy with ExecutionAndPublication: a bare GetOrAdd factory would run N duplicate full sorts
        // when a cold generation meets constant-arrival traffic.
        return generation.Sorted.GetOrAdd(
            (sortBy, order),
            key => new Lazy<string[]>(
                () => Rank(generation.Names, catalog, key.SortBy, key.Order),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    /// <summary>
    /// Marks the cached generation for the current index stale. Called from the seeded-set writers;
    /// stays a single atomic write so per-package bulk seeding pays nothing beyond it.
    /// </summary>
    public void Invalidate()
    {
        if (_caches.TryGetValue(CurrentIndex, out var cache))
            Interlocked.Increment(ref cache.Epoch);
    }

    private async Task<Generation> GetGenerationAsync(RankCache cache, CancellationToken ct)
    {
        var generation = Volatile.Read(ref cache.Generation);
        if (IsUsable(cache, generation))
            return generation;

        await cache.Gate.WaitAsync(ct);
        try
        {
            generation = Volatile.Read(ref cache.Generation);
            if (IsUsable(cache, generation))
                return generation;

            // Read the epoch before the names: a seeded-set write landing during the read bumps it, so
            // names that predate the write are served to this caller but never reused by the next one.
            var epoch = Volatile.Read(ref cache.Epoch);
            var names = await repo.ListAsync(ct);
            generation = new Generation([.. names], DateTimeOffset.UtcNow, epoch);
            Volatile.Write(ref cache.Generation, generation);
            return generation;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private static bool IsUsable(RankCache cache, [NotNullWhen(true)] Generation? generation) =>
        generation is { IsFresh: true } && generation.Epoch == Volatile.Read(ref cache.Epoch);

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

    private sealed class RankCache
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Generation? Generation;

        // Bumped by every seeded-set write; a generation carrying an older value is never reused.
        public long Epoch;
    }

    private sealed class Generation(string[] names, DateTimeOffset fetchedAt, long epoch)
    {
        public string[] Names { get; } = names;

        public long Epoch { get; } = epoch;

        // Views live on the generation so a refresh can never serve a view built from older names.
        public ConcurrentDictionary<(PackageIndexSortBy SortBy, PackageIndexSortOrder Order), Lazy<string[]>> Sorted { get; } = new();

        public bool IsFresh => DateTimeOffset.UtcNow - fetchedAt < GenerationTtl;
    }
}