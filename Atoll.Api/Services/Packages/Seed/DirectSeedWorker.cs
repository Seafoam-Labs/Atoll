using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages.Seed;

public sealed class DirectSeedWorker(
    PackageIndexStore indexStore,
    IPackageRepository repo,
    IPackageService packageService,
    IOptions<AtollOptions> options,
    ILogger<DirectSeedWorker> logger) : BackgroundService
{
    private readonly AtollOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seedDelay = TimeSpan.FromMilliseconds(Math.Max(100, _options.Seed.Direct.SeedDelayMs));

        logger.LogInformation("Direct package seeding started with a {SeedDelay} delay between packages.", seedDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            var index = indexStore.Current;

            if (index.ByNames.Count == 0)
            {
                logger.LogDebug("Package index is empty; waiting 15 seconds before the next seed attempt.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                continue;
            }

            var existing = new HashSet<string>(await repo.ListAsync(stoppingToken), StringComparer.Ordinal);
            var missing = index.ByNames.Keys.Except(existing, StringComparer.Ordinal).ToList();

            if (missing.Count == 0)
            {
                logger.LogDebug("All indexed packages are already seeded; waiting five minutes before checking again.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            logger.LogDebug("Starting a direct seed cycle for {CandidateCount} missing packages.", missing.Count);

            var seeded = 0;
            var failed = 0;
            var conflicts = 0;

            foreach (var packageName in missing)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    await packageService.SeedFromAurAsync(packageName);
                    Interlocked.Increment(ref seeded);
                    logger.LogTrace("Seeded package {PackageName}.", packageName);
                }
                catch (PackageConflictException ex)
                {
                    // Race condition: package was seeded between list and seed.
                    Interlocked.Increment(ref conflicts);
                    logger.LogDebug(ex, "Package {PackageName} was seeded by another operation.", packageName);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed to seed {PackageName}.", packageName);
                }

                await Task.Delay(seedDelay, stoppingToken);
            }

            logger.LogInformation(
                "Direct seed cycle complete: {Candidates} candidates, {Seeded} seeded, {Conflicts} already seeded, {Failed} failed.",
                missing.Count, seeded, conflicts, failed);

            if (seeded == 0)
            {
                logger.LogDebug("No packages were seeded; waiting five minutes before the next seed attempt.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("Package seeding stopped.");
    }
}