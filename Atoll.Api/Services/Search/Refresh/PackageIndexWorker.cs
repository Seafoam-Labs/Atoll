namespace Atoll.Api.Services.Search.Refresh;

public sealed class PackageIndexWorker(
    PackageIndexUpdater manager,
    ILogger<PackageIndexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await manager.InitializeFromDiskAsync(stoppingToken);

        if (File.Exists(manager.DataFilePath))
        {
            logger.LogInformation("Local data already exists. Sleeping...");
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