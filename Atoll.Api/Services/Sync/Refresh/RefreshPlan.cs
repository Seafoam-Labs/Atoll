using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog.Indexing;

namespace Atoll.Api.Services.Sync.Refresh;

public static class RefreshPlan
{
    public static IReadOnlyDictionary<string, IReadOnlyList<PackageSyncState>> GroupByPackageBase(
        IReadOnlyList<PackageSyncState> states,
        SearchIndexData index)
    {
        var result = new Dictionary<string, List<PackageSyncState>>(StringComparer.Ordinal);

        foreach (var state in states)
        {
            var pkgBase = ResolvePackageBase(index, state);
            if (!result.TryGetValue(pkgBase, out var bucket))
            {
                bucket = [];
                result[pkgBase] = bucket;
            }

            bucket.Add(state);
        }

        return result.ToDictionary(
            kv => kv.Key,
            IReadOnlyList<PackageSyncState> (kv) => kv.Value,
            StringComparer.Ordinal);
    }

    public static IReadOnlyList<CandidatePackageBase> SelectCandidates(
        IReadOnlyDictionary<string, IReadOnlyList<PackageSyncState>> grouped,
        IReadOnlyDictionary<string, string> branchHeads,
        DateTimeOffset now,
        TimeSpan maxStaleness)
    {
        var candidates = new List<CandidatePackageBase>();

        foreach (var (pkgBase, members) in grouped)
        {
            if (!branchHeads.TryGetValue(pkgBase, out var upstreamHead)) continue;

            var anyNeedsSync = members.Any(member => IsCandidate(member, upstreamHead, now, maxStaleness));
            if (!anyNeedsSync) continue;

            var headUnchanged = members.All(member =>
                string.Equals(member.LastSyncedUpstreamHead, upstreamHead, StringComparison.Ordinal));

            candidates.Add(new CandidatePackageBase(pkgBase, members, upstreamHead, headUnchanged));
        }

        return candidates;
    }

    private static bool IsCandidate(
        PackageSyncState state,
        string upstreamHead,
        DateTimeOffset now,
        TimeSpan maxStaleness)
    {
        if (string.IsNullOrEmpty(state.LastSyncedUpstreamHead))
            return true;

        if (!string.Equals(state.LastSyncedUpstreamHead, upstreamHead, StringComparison.Ordinal))
            return true;

        if (state.LastSyncSucceededAt is null)
            return true;

        return now - state.LastSyncSucceededAt.Value > maxStaleness;
    }

    private static string ResolvePackageBase(SearchIndexData index, PackageSyncState state)
    {
        if (index.ByNames.TryGetValue(state.PackageName, out var metadata)
            && !string.IsNullOrEmpty(metadata.PackageBase))
            return metadata.PackageBase;

        if (!string.IsNullOrEmpty(state.UpstreamPackageBase))
            return state.UpstreamPackageBase;

        return state.PackageName;
    }
}

public sealed record CandidatePackageBase(
    string PackageBase,
    IReadOnlyList<PackageSyncState> Members,
    string UpstreamHead,
    bool HeadUnchanged);