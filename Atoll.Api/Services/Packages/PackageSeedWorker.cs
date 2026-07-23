using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages;

public sealed class PackageSeedWorker(
    PackageIndexStore indexStore,
    IPackageRepository repo,
    IPackageService packageService,
    IOptions<AtollOptions> options,
    ILogger<PackageSeedWorker> logger) : BackgroundService
{
    private readonly AtollOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seedDelay = TimeSpan.FromMilliseconds(Math.Max(1000, _options.Seed.SeedDelayMs));

        logger.LogInformation("Package seeding started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var index = indexStore.Current;

            if (index.ByNames.Count == 0)
            {
                logger.LogInformation("Index is empty. Waiting before next attempt...");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var existing = new HashSet<string>(await repo.ListAsync(stoppingToken), StringComparer.Ordinal);
            var missing = index.ByNames.Keys.Except(existing, StringComparer.Ordinal).ToList();

            if (missing.Count == 0)
            {
                logger.LogInformation("All indexed packages are already in the database.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            logger.LogInformation("Found {Count} packages to seed.", missing.Count);

            var seeded = 0;
            var failed = 0;

            foreach (var packageName in missing)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    await packageService.SeedFromAurAsync(packageName);
                    Interlocked.Increment(ref seeded);
                    logger.LogInformation("Seeded {PackageName}.", packageName);
                }
                catch (PackageConflictException)
                {
                    // Race condition: package was seeded between list and seed.
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed to seed {PackageName}.", packageName);
                }

                await Task.Delay(seedDelay, stoppingToken);
            }

            logger.LogInformation(
                "Seed batch complete: {Seeded} seeded, {Failed} failed, {Remaining} remaining.",
                seeded,
                failed,
                missing.Count - seeded);

            if (seeded == 0)
            {
                logger.LogInformation("No packages were seeded. Waiting before next attempt...");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("Package seeding stopped.");
    }
}