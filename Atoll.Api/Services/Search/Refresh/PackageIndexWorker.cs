using Atoll.Api.Services.Search.Indexing;

namespace Atoll.Api.Services.Search.Refresh;

public sealed class PackageIndexWorker(
    PackageIndexUpdater manager,
    IAurMetadataRepository repository,
    ILogger<PackageIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await manager.InitializeAsync(stoppingToken);

        if (await repository.ExistsAsync(stoppingToken))
        {
            logger.LogInformation("Metadata already present. Sleeping before next refresh.");
            await Task.Delay(manager.RefreshInterval, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _ = await manager.DownloadAndReloadAsync(stoppingToken);
            logger.LogInformation("Sleeping...");
            await Task.Delay(manager.RefreshInterval, stoppingToken);
        }
    }
}