namespace Atoll.Api.Services.Catalog.Refresh;

public sealed class PackageIndexWorker(
    PackageIndexUpdater manager,
    ILogger<PackageIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Package index worker started with a {RefreshInterval} interval.",
            manager.RefreshInterval);

        try
        {
            await manager.InitializeAsync(stoppingToken);

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
}