namespace Atoll.Api.Services.Refresh;

public sealed class PackageRefreshWorker(
    PackageRefreshCoordinator coordinator,
    ILogger<PackageRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await coordinator.InitializeFromDiskAsync(stoppingToken);

        if (File.Exists(coordinator.DataFilePath))
        {
            logger.LogInformation("Local data already exists. Sleeping...");
            await Task.Delay(coordinator.RefreshInterval, stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _ = await coordinator.DownloadAndReloadAsync(stoppingToken);
            logger.LogInformation("Sleeping...");
            await Task.Delay(coordinator.RefreshInterval, stoppingToken);
        }
    }
}