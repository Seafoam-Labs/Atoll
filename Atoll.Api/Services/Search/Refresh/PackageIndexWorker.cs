using Atoll.Api.Services.Search.Indexing;

namespace Atoll.Api.Services.Search.Refresh;

public sealed class PackageIndexWorker(
    PackageIndexUpdater manager,
    IAurMetadataRepository repository,
    ILogger<PackageIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Package index refresh worker started with a {RefreshInterval} refresh interval.",
            manager.RefreshInterval);

        try
        {
            await manager.InitializeAsync(stoppingToken);

            if (await repository.ExistsAsync(stoppingToken))
            {
                logger.LogDebug("Cached metadata is available; waiting until the next scheduled refresh.");
                await Task.Delay(manager.RefreshInterval, stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var refreshed = await manager.DownloadAndReloadAsync(stoppingToken);
                logger.LogDebug(
                    "Package index refresh {RefreshResult}; waiting {RefreshInterval} until the next refresh.",
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