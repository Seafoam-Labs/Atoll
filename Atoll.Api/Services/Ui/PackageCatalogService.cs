using System.Collections.Immutable;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;

namespace Atoll.Api.Services.Ui;

public enum CatalogSeededFilter
{
    All,
    IndexOnly,
    Seeded
}

public enum CatalogSecurityFilter
{
    Any,
    Verified,
    Flagged,
    Pending,
    Error
}

public enum CatalogSearchMode
{
    Name,
    Words,
    Provides
}

public enum CatalogSort
{
    NameAsc,
    NameDesc,
    VotesAsc,
    VotesDesc,
    PopularityAsc,
    PopularityDesc,
    LastModifiedAsc,
    LastModifiedDesc
}

public sealed record CatalogRow(
    AurPackageMetadata Package,
    bool IsSeeded,
    HeadScanStatus? Head);

public sealed record CatalogResult(
    IReadOnlyList<CatalogRow> Rows,
    int TotalMatches,
    bool IsTruncated);

public sealed class PackageCatalogService(
    PackageIndexStore indexStore,
    IPackageService packageService,
    IPackageSecurityRepository securityRepository)
{
    public const int RenderCap = 500;

    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private SeededSnapshot _snapshot = SeededSnapshot.Empty;

    /// <summary>Drops the cached seeded/head-status snapshot so the next search re-reads it.
    /// Call after seed/rescan actions so the catalog reflects new state immediately.</summary>
    public void InvalidateSnapshot()
    {
        Volatile.Write(ref _snapshot, _snapshot with { FetchedAt = DateTimeOffset.MinValue });
    }

    public async Task<CatalogResult> SearchAsync(
        string? query,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        CatalogSearchMode mode,
        CatalogSort sort,
        CancellationToken ct = default)
    {
        var packages = indexStore.Current.ByNames.Values;

        var matches = mode switch
        {
            CatalogSearchMode.Name => FilterByName(packages, query),
            CatalogSearchMode.Words => FilterByWords(packages, query),
            CatalogSearchMode.Provides => FilterByProvides(packages, query),
            _ => packages
        };

        var packageComparer = PackageComparer(sort);

        // Seeded/security state is irrelevant when both filters are no-ops, so skip building a
        // CatalogRow (and the snapshot lookups it needs) for every match and only keep the
        // best RenderCap packages by sort key; rows are materialized only for the winners.
        if (seededFilter is CatalogSeededFilter.All && securityFilter is CatalogSecurityFilter.Any)
        {
            var topPackages = new BoundedTopK<AurPackageMetadata>(RenderCap, packageComparer);
            var total = 0;

            foreach (var package in matches)
            {
                total++;
                topPackages.Offer(package);
            }

            var snapshot = await GetSeededSnapshotAsync(ct);
            var rendered = topPackages.ExtractSorted()
                .Select(package => new CatalogRow(
                    package,
                    snapshot.SeededNames.Contains(package.Name),
                    snapshot.HeadStatuses.GetValueOrDefault(package.Name)))
                .ToList();

            return new CatalogResult(rendered, total, total > RenderCap);
        }

        return await SearchFilteredAsync(matches, seededFilter, securityFilter, packageComparer, ct);
    }

    private async Task<CatalogResult> SearchFilteredAsync(
        IEnumerable<AurPackageMetadata> matches,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        IComparer<AurPackageMetadata> packageComparer,
        CancellationToken ct)
    {
        var snapshot = await GetSeededSnapshotAsync(ct);
        var rowComparer = Comparer<CatalogRow>.Create((a, b) => packageComparer.Compare(a.Package, b.Package));
        var topRows = new BoundedTopK<CatalogRow>(RenderCap, rowComparer);
        var total = 0;

        foreach (var package in matches)
        {
            var row = new CatalogRow(
                package,
                snapshot.SeededNames.Contains(package.Name),
                snapshot.HeadStatuses.GetValueOrDefault(package.Name));

            if (!MatchesSeededFilter(row, seededFilter)) continue;
            if (!MatchesSecurityFilter(row, securityFilter)) continue;

            total++;
            topRows.Offer(row);
        }

        var rendered = topRows.ExtractSorted();
        return new CatalogResult(rendered, total, total > RenderCap);
    }

    private static bool MatchesSeededFilter(CatalogRow row, CatalogSeededFilter filter)
    {
        return filter switch
        {
            CatalogSeededFilter.IndexOnly => !row.IsSeeded,
            CatalogSeededFilter.Seeded => row.IsSeeded,
            _ => true
        };
    }

    private static bool MatchesSecurityFilter(CatalogRow row, CatalogSecurityFilter filter)
    {
        // Scan state exists only for seeded packages; the security filter narrows to that subset.
        if (filter is CatalogSecurityFilter.Any) return true;

        return row.IsSeeded && row.Head?.Status == ToSecurityStatus(filter);
    }

    private static SecurityStatus ToSecurityStatus(CatalogSecurityFilter filter)
    {
        return filter switch
        {
            CatalogSecurityFilter.Verified => SecurityStatus.Verified,
            CatalogSecurityFilter.Flagged => SecurityStatus.Flagged,
            CatalogSecurityFilter.Pending => SecurityStatus.Pending,
            CatalogSecurityFilter.Error => SecurityStatus.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null)
        };
    }

    private static IEnumerable<AurPackageMetadata> FilterByName(
        IEnumerable<AurPackageMetadata> packages, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return packages;

        return packages.Where(package =>
            package.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            package.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<AurPackageMetadata> FilterByWords(
        IEnumerable<AurPackageMetadata> packages, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return packages;

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.Length == 0
            ? packages
            : packages.Where(package => tokens.All(token => MatchesWord(package, token)));
    }

    private static bool MatchesWord(AurPackageMetadata package, string token)
    {
        return package.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
               || package.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
               || package.Provides.Any(provides => provides.Contains(token, StringComparison.OrdinalIgnoreCase))
               || package.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<AurPackageMetadata> FilterByProvides(
        IEnumerable<AurPackageMetadata> packages, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return packages;

        return packages.Where(package =>
            package.Provides.Any(provides => provides.Contains(query, StringComparison.OrdinalIgnoreCase)));
    }

    private static IComparer<AurPackageMetadata> PackageComparer(CatalogSort sort)
    {
        return sort switch
        {
            CatalogSort.NameAsc => Comparer<AurPackageMetadata>.Create(
                (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)),
            CatalogSort.NameDesc => Comparer<AurPackageMetadata>.Create(
                (a, b) => string.Compare(b.Name, a.Name, StringComparison.OrdinalIgnoreCase)),
            CatalogSort.VotesAsc => Comparer<AurPackageMetadata>.Create((a, b) => a.NumVotes.CompareTo(b.NumVotes)),
            CatalogSort.VotesDesc => Comparer<AurPackageMetadata>.Create((a, b) => b.NumVotes.CompareTo(a.NumVotes)),
            CatalogSort.PopularityAsc => Comparer<AurPackageMetadata>.Create((a, b) => a.Popularity.CompareTo(b.Popularity)),
            CatalogSort.PopularityDesc => Comparer<AurPackageMetadata>.Create((a, b) => b.Popularity.CompareTo(a.Popularity)),
            CatalogSort.LastModifiedAsc => Comparer<AurPackageMetadata>.Create((a, b) => a.LastModified.CompareTo(b.LastModified)),
            CatalogSort.LastModifiedDesc => Comparer<AurPackageMetadata>.Create((a, b) => b.LastModified.CompareTo(a.LastModified)),
            _ => Comparer<AurPackageMetadata>.Create(
                (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase))
        };
    }

    /// <summary>Keeps only the best <c>capacity</c> items seen so far, per <paramref name="comparer"/>,
    /// using a bounded max-heap so the running cost is O(N log capacity) instead of a full O(N log N) sort.</summary>
    private sealed class BoundedTopK<T>(int capacity, IComparer<T> comparer)
    {
        // Reversed comparer: the heap's min (its Peek) is the worst item per the caller's comparer,
        // so it's the one to evict when a better candidate arrives.
        private readonly PriorityQueue<T, T> _heap = new(Comparer<T>.Create((a, b) => -comparer.Compare(a, b)));

        public void Offer(T item)
        {
            if (_heap.Count < capacity)
            {
                _heap.Enqueue(item, item);
                return;
            }

            if (comparer.Compare(item, _heap.Peek()) < 0)
                _heap.EnqueueDequeue(item, item);
        }

        public List<T> ExtractSorted()
        {
            var list = new List<T>(_heap.Count);
            while (_heap.TryDequeue(out var item, out _))
                list.Add(item);
            list.Sort(comparer);
            return list;
        }
    }

    private async Task<SeededSnapshot> GetSeededSnapshotAsync(CancellationToken ct)
    {
        var current = Volatile.Read(ref _snapshot);
        if (current.IsFresh) return current;

        await _snapshotGate.WaitAsync(ct);
        try
        {
            current = Volatile.Read(ref _snapshot);
            if (current.IsFresh) return current;

            var seeded = await packageService.ListAsync();
            var heads = await securityRepository.ListHeadStatusesAsync(ct);

            var headStatuses = ImmutableDictionary.CreateBuilder<string, HeadScanStatus>(StringComparer.Ordinal);
            foreach (var head in heads)
                headStatuses[head.PackageName] = head;

            var next = new SeededSnapshot(
                seeded.ToImmutableHashSet(StringComparer.Ordinal),
                headStatuses.ToImmutable(),
                DateTimeOffset.UtcNow);

            Volatile.Write(ref _snapshot, next);
            return next;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private sealed record SeededSnapshot(
        ImmutableHashSet<string> SeededNames,
        ImmutableDictionary<string, HeadScanStatus> HeadStatuses,
        DateTimeOffset FetchedAt)
    {
        public static SeededSnapshot Empty { get; } = new(
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal),
            ImmutableDictionary<string, HeadScanStatus>.Empty.WithComparers(StringComparer.Ordinal),
            DateTimeOffset.MinValue);

        public bool IsFresh =>
            FetchedAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow - FetchedAt < SnapshotTtl;
    }
}