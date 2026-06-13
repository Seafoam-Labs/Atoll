using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api.Controllers;

[ApiController]
public sealed class MetricsController(
    PackageIndexStore store,
    PackageQueryService queryService,
    PackageRefreshCoordinator refreshCoordinator,
    ApplicationRuntimeInfo runtimeInfo) : ControllerBase
{
    [HttpGet("/metrics")]
    public IActionResult Metrics()
    {
        var snapshot = store.Current;
        var refresh = refreshCoordinator.GetStatus();

        var response = new MetricsResponse
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

        return Ok(response);
    }
}
