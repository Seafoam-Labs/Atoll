namespace Atoll.Api.Services.Metrics;

public sealed class MetricsService(
    PackageIndexStore store,
    PackageQueryService queryService,
    PackageRefreshCoordinator refreshCoordinator,
    ApplicationRuntimeInfo runtimeInfo)
{
    public MetricsResponse GetMetrics()
    {
        var snapshot = store.Current;
        var refresh = refreshCoordinator.GetStatus();

        return new MetricsResponse
        {
            UptimeSeconds = (long)(DateTimeOffset.UtcNow - runtimeInfo.StartedAtUtc).TotalSeconds,
            RequestCount = queryService.RequestCount,
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