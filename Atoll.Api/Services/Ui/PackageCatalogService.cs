using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
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
    int Page,
    int PageSize,
    int TotalPages);

public sealed class PackageCatalogService(
    PackageIndexStore indexStore,
    IPackageService packageService,
    IPackageSecurityRepository securityRepository)
{
    public const int PageSize = 50;

    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private SeededSnapshot _snapshot = SeededSnapshot.Empty;

    // Sorted package arrays cached per (index instance, sort). PackageIndexStore.Replace swaps the
    // whole SearchIndexData, and ConditionalWeakTable keys on that instance, so a generation's
    // views are dropped automatically once the replaced index is no longer referenced.
    private readonly ConditionalWeakTable<SearchIndexData, ConcurrentDictionary<CatalogSort, AurPackageMetadata[]>>
        _sortedViews = new();

    /// <summary>Invalidates the cached seeded/head-status snapshot.</summary>
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
        int page = 1,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;

        var index = indexStore.Current;
        var sorted = GetSortedPackages(index, sort);
        var matches = BuildPredicate(mode, query);
        var snapshot = await GetSeededSnapshotAsync(ct);

        var result = CollectPage(sorted, matches, seededFilter, securityFilter, snapshot, page);

        // Clamp out-of-range pages to the last valid page.
        return page > result.TotalPages
            ? CollectPage(sorted, matches, seededFilter, securityFilter, snapshot, result.TotalPages)
            : result;
    }

    private static CatalogResult CollectPage(
        AurPackageMetadata[] sorted,
        Func<AurPackageMetadata, bool>? matches,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        SeededSnapshot snapshot,
        int page)
    {
        var needsRowFilters =
            seededFilter is not CatalogSeededFilter.All || securityFilter is not CatalogSecurityFilter.Any;
        var start = (long)(page - 1) * PageSize;

        List<AurPackageMetadata>? window = null;
        int total;

        // Fast path: direct slice for unfiltered views.
        if (matches is null && !needsRowFilters)
        {
            total = sorted.Length;
            var begin = (int)Math.Min(start, total);
            var end = (int)Math.Min(start + PageSize, total);
            window = new List<AurPackageMetadata>(end - begin);
            for (var i = begin; i < end; i++)
                window.Add(sorted[i]);
        }
        else
        {
            total = 0;
            foreach (var package in sorted)
            {
                if (matches is not null && !matches(package)) continue;
                if (needsRowFilters && !MatchesRowFilters(package, seededFilter, securityFilter, snapshot)) continue;

                if (total >= start && total < start + PageSize)
                    (window ??= new List<AurPackageMetadata>(PageSize)).Add(package);
                total++;
            }
        }

        var rows = new List<CatalogRow>(window?.Count ?? 0);
        if (window is not null)
        {
            foreach (var package in window)
            {
                rows.Add(new CatalogRow(
                    package,
                    snapshot.SeededNames.Contains(package.Name),
                    snapshot.HeadStatuses.GetValueOrDefault(package.Name)));
            }
        }

        return new CatalogResult(rows, total, page, PageSize, TotalPages(total));
    }

    private static bool MatchesRowFilters(
        AurPackageMetadata package,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        SeededSnapshot snapshot)
    {
        var isSeeded = snapshot.SeededNames.Contains(package.Name);

        var passesSeeded = seededFilter switch
        {
            CatalogSeededFilter.IndexOnly => !isSeeded,
            CatalogSeededFilter.Seeded => isSeeded,
            _ => true
        };
        if (!passesSeeded) return false;

        if (securityFilter is CatalogSecurityFilter.Any) return true;

        return isSeeded
               && snapshot.HeadStatuses.GetValueOrDefault(package.Name)?.Status == ToSecurityStatus(securityFilter);
    }

    private static int TotalPages(int total) => Math.Max(1, (total + PageSize - 1) / PageSize);

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

    private AurPackageMetadata[] GetSortedPackages(SearchIndexData index, CatalogSort sort)
    {
        var bySort = _sortedViews.GetValue(
            index, static _ => new ConcurrentDictionary<CatalogSort, AurPackageMetadata[]>());

        // Benign race: concurrent sorts produce identical arrays.
        return bySort.GetOrAdd(
            sort,
            static (s, values) =>
            {
                var packages = values.ToArray();
                Array.Sort(packages, PackageComparer(s));
                return packages;
            },
            index.ByNames.Values);
    }

    private static Func<AurPackageMetadata, bool>? BuildPredicate(CatalogSearchMode mode, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        return mode switch
        {
            CatalogSearchMode.Name => package =>
                package.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                package.Description.Contains(query, StringComparison.OrdinalIgnoreCase),
            CatalogSearchMode.Words => BuildWordsPredicate(query),
            CatalogSearchMode.Provides => package =>
                package.Provides.Any(provides => provides.Contains(query, StringComparison.OrdinalIgnoreCase)),
            _ => null
        };
    }

    private static Func<AurPackageMetadata, bool> BuildWordsPredicate(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return package => tokens.All(token =>
            package.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
            || package.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
            || package.Provides.Any(provides => provides.Contains(token, StringComparison.OrdinalIgnoreCase))
            || package.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly Comparison<AurPackageMetadata> ByName =
        (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private static IComparer<AurPackageMetadata> PackageComparer(CatalogSort sort)
    {
        // Array.Sort is unstable; tie-break on name for deterministic paging.
        return sort switch
        {
            CatalogSort.NameAsc => Comparer<AurPackageMetadata>.Create(ByName),
            CatalogSort.NameDesc => Comparer<AurPackageMetadata>.Create((a, b) => ByName(b, a)),
            CatalogSort.VotesAsc => Comparer<AurPackageMetadata>.Create(
                (a, b) => a.NumVotes.CompareTo(b.NumVotes) is var c && c != 0 ? c : ByName(a, b)),
            CatalogSort.VotesDesc => Comparer<AurPackageMetadata>.Create(
                (a, b) => b.NumVotes.CompareTo(a.NumVotes) is var c && c != 0 ? c : ByName(a, b)),
            CatalogSort.PopularityAsc => Comparer<AurPackageMetadata>.Create(
                (a, b) => a.Popularity.CompareTo(b.Popularity) is var c && c != 0 ? c : ByName(a, b)),
            CatalogSort.PopularityDesc => Comparer<AurPackageMetadata>.Create(
                (a, b) => b.Popularity.CompareTo(a.Popularity) is var c && c != 0 ? c : ByName(a, b)),
            CatalogSort.LastModifiedAsc => Comparer<AurPackageMetadata>.Create(
                (a, b) => a.LastModified.CompareTo(b.LastModified) is var c && c != 0 ? c : ByName(a, b)),
            CatalogSort.LastModifiedDesc => Comparer<AurPackageMetadata>.Create(
                (a, b) => b.LastModified.CompareTo(a.LastModified) is var c && c != 0 ? c : ByName(a, b)),
            _ => Comparer<AurPackageMetadata>.Create(ByName)
        };
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
