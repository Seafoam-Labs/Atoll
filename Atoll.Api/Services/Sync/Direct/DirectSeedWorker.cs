using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Sync.Direct;

internal enum DirectSeedCycleOutcome
{
    Completed,
    IndexEmpty,
    NothingMissing
}

internal sealed record DirectSeedCycleResult(DirectSeedCycleOutcome Outcome, int Seeded);

public sealed class DirectSeedWorker(
    PackageIndexStore indexStore,
    IPackageRepository repo,
    DirectPackageSeeder seeder,
    DirectSeedStatusStore status,
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
            DirectSeedCycleResult result;

            try
            {
                result = await RunCycleAsync(seedDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Direct seed cycle failed; retrying in five minutes.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                continue;
            }

            // Retry failed packages immediately only when the cycle made progress; otherwise back off.
            if (result.Outcome == DirectSeedCycleOutcome.IndexEmpty)
            {
                logger.LogDebug("Package index is empty; waiting 15 seconds before the next seed attempt.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            else if (result.Outcome == DirectSeedCycleOutcome.NothingMissing)
            {
                logger.LogDebug("All indexed packages are present; checking for newly indexed packages in one minute.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            else if (result.Seeded == 0)
            {
                logger.LogDebug("No packages were seeded; waiting five minutes before retrying failures.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("Package seeding stopped.");
    }

    internal async Task<DirectSeedCycleResult> RunCycleAsync(TimeSpan seedDelay, CancellationToken stoppingToken)
    {
        var index = indexStore.Current;

        if (index.ByNames.Count == 0)
        {
            logger.LogDebug("Package index is empty; skipping direct seed cycle.");
            return new DirectSeedCycleResult(DirectSeedCycleOutcome.IndexEmpty, 0);
        }

        var existing = new HashSet<string>(await repo.ListAsync(stoppingToken), StringComparer.Ordinal);
        var missing = index.ByNames.Keys.Except(existing, StringComparer.Ordinal).ToList();

        if (missing.Count == 0)
        {
            logger.LogDebug("All indexed packages are already seeded; skipping direct seed cycle.");
            return new DirectSeedCycleResult(DirectSeedCycleOutcome.NothingMissing, 0);
        }

        logger.LogDebug("Starting a direct seed cycle for {CandidateCount} missing packages.", missing.Count);

        status.BeginCycle(missing.Count);
        var seeded = 0;
        var failed = 0;
        var alreadyPresent = 0;

        try
        {
            foreach (var packageName in missing)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    await seeder.SeedAsync(packageName, stoppingToken);
                    seeded++;
                    status.RecordSeeded();
                    logger.LogTrace("Seeded package {PackageName}.", packageName);
                }
                catch (PackageConflictException ex)
                {
                    // Race condition: package was seeded between list and seed.
                    alreadyPresent++;
                    status.RecordAlreadyPresent();
                    logger.LogDebug(ex, "Package {PackageName} was seeded by another operation.", packageName);
                }
                catch (Exception ex)
                {
                    failed++;
                    status.RecordFailed();
                    logger.LogWarning(ex, "Failed to seed {PackageName}.", packageName);
                }

                await Task.Delay(seedDelay, stoppingToken);
            }
        }
        finally
        {
            status.EndCycle();
        }

        logger.LogInformation(
            "Direct seed cycle complete: {Candidates} candidates, {Seeded} seeded, {AlreadyPresent} already seeded, {Failed} failed.",
            missing.Count, seeded, alreadyPresent, failed);

        return new DirectSeedCycleResult(DirectSeedCycleOutcome.Completed, seeded);
    }
}
