namespace Atoll.Api.Services.Packages.Storage;

public class PackageSyncStorageWorker(
    IPackageService repo,
    ILogger<PackageSyncStorageWorker> logger
) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);

            try
            {
                foreach (var package in await repo.ListAsync())
                {
                    await repo.SyncToStorageAsync(package);
                    logger.LogInformation("Synced {Package} to Storage", package);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Package sync failed");
            }
        }
    }
}