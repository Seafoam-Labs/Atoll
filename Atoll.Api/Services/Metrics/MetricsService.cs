using Atoll.Api.Services.Runtime;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;

namespace Atoll.Api.Services.Metrics;

public sealed class MetricsService(
    PackageIndexStore store,
    PackageSearchService searchService,
    PackageIndexUpdater updater,
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
                DataFile = refresh.DataFile,
                IntervalSeconds = (long)refresh.Interval.TotalSeconds,
                Attempts = refresh.Attempts,
                Successes = refresh.Successes,
                Failures = refresh.Failures,
                LastStartedUtc = refresh.LastStartedUtc,
                LastSucceededUtc = refresh.LastSucceededUtc,
                LastFailedUtc = refresh.LastFailedUtc
            }
        };
    }
}