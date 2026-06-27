namespace Atoll.Api.Services.Aur;

public class PackageSyncS3Worker(
    IPackageRepository repo,
    ILogger<PackageSyncS3Worker> logger
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
                    logger.LogInformation("Synced {Package} to S3", package);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Package sync failed");
            }
        }
    }
}