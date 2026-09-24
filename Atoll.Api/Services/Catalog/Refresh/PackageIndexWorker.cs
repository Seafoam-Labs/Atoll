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
            await PrewarmCachesAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var refreshed = await manager.DownloadAndReloadAsync(stoppingToken);
                logger.LogDebug(
                    "Package index refresh {RefreshResult}; waiting {RefreshInterval} until the next interval.",
                    refreshed ? "completed" : "failed", manager.RefreshInterval);
                await Task.Delay(manager.RefreshInterval, stoppingToken);
            }
        }
        finally
        {
            logger.LogInformation("Package index refresh worker stopped.");
        }
    }

    // Runs once, before any request: the indexed caches would otherwise be built inside a visitor's
    // request. A failure falls back to building either of them on demand.
    private async Task PrewarmCachesAsync(CancellationToken ct)
    {
        try
        {
            await catalog.PrewarmAsync(ct);
            await packageService.PrewarmAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Cache prewarm after the initial index load failed; requests will build the caches on demand.");
        }
    }
}