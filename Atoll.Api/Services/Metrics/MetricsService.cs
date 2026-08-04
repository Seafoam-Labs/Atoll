using Atoll.Api.Services.Packages.Refresh;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Runtime;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Services.Security;

namespace Atoll.Api.Services.Metrics;

public sealed class MetricsService(
    PackageIndexStore store,
    PackageSearchService searchService,
    PackageIndexUpdater updater,
    BulkSeedStatusStore bulkSeedStatus,
    RefreshStatusStore packageRefreshStatus,
    SecurityScanStatusStore securityScanStatus,
    ApplicationRuntimeInfo runtimeInfo)
{
    public Metrics GetMetrics()
    {
        var snapshot = store.Current;
        var refresh = updater.GetStatus();

        return new Metrics
        {
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - runtimeInfo.StartedAtUtc).TotalSeconds,
            RequestCount = searchService.RequestCount,
            IndexSizes = new IndexSizes
            {
                ByNames = snapshot.ByNames.Count,
                ByProvides = snapshot.ByProvides.Count,
                ByWords = snapshot.ByWords.Count
            },
            Refresh = new RefreshStatus
            {
                MetadataCollection = refresh.MetadataCollection,
                IntervalSeconds = (long)refresh.Interval.TotalSeconds,
                Attempts = refresh.Attempts,
                Successes = refresh.Successes,
                Failures = refresh.Failures,
                LastStartedUtc = refresh.LastStartedUtc,
                LastSucceededUtc = refresh.LastSucceededUtc,
                LastFailedUtc = refresh.LastFailedUtc
            },
            BulkSeed = MapBulkSeed(bulkSeedStatus.GetSnapshot()),
            PackageRefresh = MapPackageRefresh(packageRefreshStatus.GetSnapshot()),
            SecurityScan = MapSecurityScan(securityScanStatus.GetSnapshot())
        };
    }

    private static BulkSeedStatus MapBulkSeed(BulkSeedStatusSnapshot s)
    {
        return new BulkSeedStatus
        {
            Enabled = s.Enabled,
            BatchesAttempted = s.BatchesAttempted,
            BatchesSucceeded = s.BatchesSucceeded,
            BatchesFailed = s.BatchesFailed,
            RefsSkipped = s.RefsSkipped,
            RefsFailed = s.RefsFailed,
            PackagesSeeded = s.PackagesSeeded,
            PackagesSkipped = s.PackagesSkipped,
            PackagesExcluded = s.PackagesExcluded,
            LastStartedUtc = s.LastStartedUtc,
            LastFinishedUtc = s.LastFinishedUtc
        };
    }

    private static PackageRefreshStatus MapPackageRefresh(PackageRefreshStatusSnapshot s)
    {
        return new PackageRefreshStatus
        {
            Enabled = s.Enabled,
            CyclesAttempted = s.CyclesAttempted,
            CyclesSucceeded = s.CyclesSucceeded,
            CyclesFailed = s.CyclesFailed,
            CandidatePackages = s.CandidatePackages,
            CandidatePackageBases = s.CandidatePackageBases,
            PackagesUpdated = s.PackagesUpdated,
            PackagesUnchanged = s.PackagesUnchanged,
            PackagesSkipped = s.PackagesSkipped,
            RefsSkipped = s.RefsSkipped,
            RefsFailed = s.RefsFailed,
            LastStartedUtc = s.LastStartedUtc,
            LastFinishedUtc = s.LastFinishedUtc
        };
    }

    private static SecurityScanStatus MapSecurityScan(SecurityScanStatusSnapshot s)
    {
        return new SecurityScanStatus
        {
            Enabled = s.Enabled,
            ScansCompleted = s.ScansCompleted,
            ScansVerified = s.ScansVerified,
            ScansFlagged = s.ScansFlagged,
            ScansErrored = s.ScansErrored,
            ScansDropped = s.ScansDropped,
            PendingScans = s.PendingScans,
            LastScanFinishedUtc = s.LastScanFinishedUtc
        };
    }
}