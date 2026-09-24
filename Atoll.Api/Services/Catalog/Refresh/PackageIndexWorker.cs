using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Ui;

namespace Atoll.Api.Services.Catalog.Refresh;

public sealed class PackageIndexWorker(
    PackageIndexUpdater manager,
    PackageCatalogService catalog,
    IPackageService packageService,
    ILogger<PackageIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Package index worker started with a {RefreshInterval} interval.",
            manager.RefreshInterval);

        try
        {
            await manager.InitializeAsync(stoppingToken);
            await WarmCachesAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var outcome = await RefreshAndWarmAsync(stoppingToken);

                logger.LogDebug(
                    "Package index refresh {RefreshOutcome}; waiting {RefreshInterval} until the next interval.",
                    outcome, manager.RefreshInterval);
                await Task.Delay(manager.RefreshInterval, stoppingToken);
            }
        }
        finally
        {
            logger.LogInformation("Package index refresh worker stopped.");
        }
    }

    /// <summary>
    ///     One refresh cycle plus the warm that follows it. A 304 warms too: the ranker's warm re-arms
    ///     the TTL of everything it re-stores, so skipping unchanged cycles would let those entries run
    ///     out into a request, and a 304 cycle can still have pruned packages. Only a failed fetch
    ///     leaves the caches alone.
    /// </summary>
    internal async Task<PackageIndexRefreshOutcome> RefreshAndWarmAsync(CancellationToken ct)
    {
        var outcome = await manager.DownloadAndReloadAsync(ct);
        if (outcome is not PackageIndexRefreshOutcome.Failed)
            await WarmCachesAsync(ct);

        return outcome;
    }

    // Startup and post-cycle warm, before any request has to build a view. A cycle that pruned nothing
    // leaves the seeded snapshot and the name list as cache hits; the generation-keyed sorted views
    // are rebuilt either way. A warm that throws leaves the caches to be built on demand.
    private async Task WarmCachesAsync(CancellationToken ct)
    {
        try
        {
            await catalog.PrewarmAsync(ct);
            await packageService.PrewarmAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache warm after an index change failed; requests will build the caches on demand.");
        }
    }
}
