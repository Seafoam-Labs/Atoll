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

    public async Task<CatalogResult> SearchAsync(
        string? query,
        CatalogSeededFilter seededFilter,
        CatalogSecurityFilter securityFilter,
        CatalogSearchMode mode,
        CatalogSort sort,
        CancellationToken ct = default)
    {
        var snapshot = await GetSeededSnapshotAsync(ct);
        var packages = indexStore.Current.ByNames.Values;

        var matches = mode switch
        {
            CatalogSearchMode.Name => FilterByName(packages, query),
            CatalogSearchMode.Words => FilterByWords(packages, query),
            CatalogSearchMode.Provides => FilterByProvides(packages, query),
            _ => packages
        };

        var rows = new List<CatalogRow>(RenderCap);
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
            rows.Add(row);
        }

        var sorted = Sort(rows, sort);
        var rendered = total > RenderCap ? [.. sorted.Take(RenderCap)] : sorted;

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

    private static List<CatalogRow> Sort(List<CatalogRow> rows, CatalogSort sort)
    {
        return sort switch
        {
            CatalogSort.NameAsc => [.. rows.OrderBy(row => row.Package.Name, StringComparer.OrdinalIgnoreCase)],
            CatalogSort.NameDesc => [.. rows.OrderByDescending(row => row.Package.Name, StringComparer.OrdinalIgnoreCase)],
            CatalogSort.VotesAsc => [.. rows.OrderBy(row => row.Package.NumVotes)],
            CatalogSort.VotesDesc => [.. rows.OrderByDescending(row => row.Package.NumVotes)],
            CatalogSort.PopularityAsc => [.. rows.OrderBy(row => row.Package.Popularity)],
            CatalogSort.PopularityDesc => [.. rows.OrderByDescending(row => row.Package.Popularity)],
            CatalogSort.LastModifiedAsc => [.. rows.OrderBy(row => row.Package.LastModified)],
            CatalogSort.LastModifiedDesc => [.. rows.OrderByDescending(row => row.Package.LastModified)],
            _ => [.. rows.OrderBy(row => row.Package.Name, StringComparer.OrdinalIgnoreCase)]
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

            var headStatuses = new Dictionary<string, HeadScanStatus>(StringComparer.Ordinal);
            foreach (var head in heads)
                headStatuses[head.PackageName] = head;

            var next = new SeededSnapshot(
                seeded.ToImmutableHashSet(StringComparer.Ordinal),
                headStatuses.ToImmutableDictionary(),
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