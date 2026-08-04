using Atoll.Api.Services.Packages.Mirror;
using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages.Seed;

public sealed class PackageBulkSeedWorker(
    PackageIndexStore indexStore,
    IPackageRepository repo,
    IPackageService packageService,
    ISeedExclusionRepository exclusions,
    IAurMirror mirror,
    BulkSeedStatusStore status,
    IOptions<AtollOptions> options,
    ILogger<PackageBulkSeedWorker> logger)
    : BackgroundService
{
    private readonly BulkSeedOptions _options = options.Value.Seed.Bulk;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batchDelay = TimeSpan.FromMilliseconds(Math.Max(100, _options.BatchDelayMs));
        var batchSize = Math.Clamp(_options.BatchSize, 10, 10_000);

        logger.LogInformation(
            "Bulk package seeding started with a batch size of {BatchSize}, a {BatchDelay} batch delay, and direct AUR fallback {AurFallbackEnabled}.",
            batchSize, batchDelay, _options.AurFallbackForNotOnMirror ? "enabled" : "disabled");

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                var outcome = await RunCycleAsync(batchSize, batchDelay, stoppingToken);
                if (outcome is { backedOff: false, packagesSeeded: 0 })
                {
                    logger.LogDebug("No packages were seeded; waiting five minutes before the next bulk seed cycle.");
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bulk seed cycle failed; will retry after backoff.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }

        logger.LogInformation("Bulk package seeding stopped.");
    }

    internal async Task<(int packagesSeeded, int packagesSkipped, bool backedOff)> RunCycleAsync(
        int batchSize, TimeSpan batchDelay, CancellationToken stoppingToken)
    {
        var index = indexStore.Current;
        if (index.ByNames.Count == 0)
        {
            logger.LogDebug("Package index is empty; waiting 15 seconds before the next bulk seed cycle.");
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            return (0, 0, backedOff: true);
        }

        status.BeginCycle();

        var packagesSeeded = 0;
        var packagesSkipped = 0;
        var packagesExcluded = 0;

        try
        {
            await mirror.EnsureInitializedAsync(stoppingToken);

            var existing = new HashSet<string>(await repo.ListAsync(stoppingToken), StringComparer.Ordinal);
            var missing = index.ByNames.Keys.Except(existing, StringComparer.Ordinal).ToList();

            if (missing.Count == 0)
            {
                logger.LogDebug("All indexed packages are already seeded; waiting five minutes before checking again.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                return (0, 0, backedOff: true);
            }

            var targets = BulkSeedPlan.BuildPkgBaseTargets(missing, name => ResolvePackageBase(index, name));
            var excludedBases = await exclusions.ListDocumentTooLargePackageBasesAsync(stoppingToken);
            packagesExcluded = targets.Where(x => excludedBases.Contains(x.Key)).Sum(x => x.Value.Count);
            targets = targets.Where(x => !excludedBases.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var seedablePackageCount = targets.Values.Sum(x => x.Count);

            logger.LogDebug(
                "Bulk seed plan: {SeedablePackageCount} packages to seed across {PackageBaseCount} pkgbases; {ExcludedPackageCount} packages excluded due to document size.",
                seedablePackageCount, targets.Count, packagesExcluded);

            if (targets.Count == 0)
            {
                logger.LogDebug("All missing packages are permanently excluded; waiting five minutes before the next bulk seed cycle.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                return (0, 0, backedOff: true);
            }

            var branches = await mirror.ListBranchesAsync(stoppingToken);

            var pkgBases = targets.Keys.ToList();
            var fetchable = pkgBases.Where(branches.Contains).ToList();
            var notOnMirror = pkgBases.Except(fetchable, StringComparer.Ordinal).ToList();
            var refsSkipped = notOnMirror.Count;

            if (refsSkipped > 0)
            {
                status.AddRefsSkipped(refsSkipped);
                logger.LogDebug("{PackageBaseCount} pkgbases have no mirror branch; they will be {Handling}.",
                    refsSkipped, _options.AurFallbackForNotOnMirror ? "seeded through the direct AUR fallback" : "skipped");
            }

            if (_options.AurFallbackForNotOnMirror)
                foreach (var pkgBase in notOnMirror)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    var (seeded, skipped, excluded) = await SeedViaDirectCloneAsync(pkgBase, targets[pkgBase], stoppingToken);
                    packagesSeeded += seeded;
                    packagesSkipped += skipped;
                    packagesExcluded += excluded;
                }
            else
                packagesSkipped += notOnMirror.Sum(b => targets[b].Count);

            foreach (var batch in BulkSeedPlan.ChunkBy(fetchable, batchSize))
            {
                if (stoppingToken.IsCancellationRequested) break;

                status.RecordBatchAttempted();

                BulkFetchResult result;
                try
                {
                    result = await mirror.FetchAsync(batch, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status.RecordBatchFailed();
                    logger.LogWarning(ex, "Batch fetch of {Count} pkgbases failed entirely; skipping batch.", batch.Count);
                    packagesSkipped += batch.Sum(b => targets[b].Count);
                    await Task.Delay(batchDelay, stoppingToken);
                    continue;
                }

                status.RecordBatchSucceeded();
                status.AddRefsFailed(result.Failed.Count);
                packagesSkipped += result.Failed.Sum(b => targets[b].Count);

                foreach (var pkgBase in result.Succeeded)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    IReadOnlyDictionary<string, string> files;
                    try
                    {
                        files = await mirror.ReadFilesAsync(pkgBase, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not read files for pkgbase {PkgBase}; skipping.", pkgBase);
                        packagesSkipped += targets[pkgBase].Count;
                        continue;
                    }

                    foreach (var packageName in targets[pkgBase])
                        try
                        {
                            await packageService.SeedFilesAsync(packageName, files);
                            packagesSeeded++;
                        }
                        catch (PackageConflictException)
                        {
                            // Race: seeded between list and seed. Not an error.
                        }
                        catch (PackageDocumentTooLargeException ex)
                        {
                            await exclusions.RecordDocumentTooLargeAsync(pkgBase, targets[pkgBase], ex.SerializedSizeBytes, stoppingToken);
                            packagesExcluded += targets[pkgBase].Count;
                            logger.LogWarning(ex,
                                "Excluded pkgbase {PkgBase} from future bulk seed cycles because its {SizeBytes}-byte package document exceeds MongoDB's limit.",
                                pkgBase, ex.SerializedSizeBytes);
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to seed {PackageName}.", packageName);
                            packagesSkipped++;
                        }
                }

                logger.LogDebug(
                    "Bulk seed batch complete: {FetchedPackageBaseCount} pkgbases fetched, {FailedPackageBaseCount} failed, {PackagesSeededSoFar} packages seeded so far.",
                    result.Succeeded.Count, result.Failed.Count, packagesSeeded);

                await Task.Delay(batchDelay, stoppingToken);
            }

            logger.LogInformation(
                "Bulk seed cycle complete: {SeededPackageCount} seeded, {SkippedPackageCount} skipped, {ExcludedPackageCount} excluded.",
                packagesSeeded, packagesSkipped, packagesExcluded);

            return (packagesSeeded, packagesSkipped, backedOff: false);
        }
        finally
        {
            status.AddPackagesSeeded(packagesSeeded);
            status.AddPackagesSkipped(packagesSkipped);
            status.AddPackagesExcluded(packagesExcluded);
            status.EndCycle();
        }
    }

    private async Task<(int seeded, int skipped, int excluded)> SeedViaDirectCloneAsync(
        string packageBase,
        IReadOnlyList<string> packageNames,
        CancellationToken ct)
    {
        var seeded = 0;
        var skipped = 0;
        var excluded = 0;

        foreach (var packageName in packageNames)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await packageService.SeedFromAurAsync(packageName);
                seeded++;
            }
            catch (PackageConflictException)
            {
                // Race.
            }
            catch (PackageDocumentTooLargeException ex)
            {
                await exclusions.RecordDocumentTooLargeAsync(packageBase, packageNames, ex.SerializedSizeBytes, ct);
                excluded = packageNames.Count;
                logger.LogWarning(ex,
                    "Excluded pkgbase {PkgBase} from future seed cycles because its {SizeBytes}-byte package document exceeds MongoDB's limit.",
                    packageBase, ex.SerializedSizeBytes);
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Direct AUR fallback failed for {PackageName}.", packageName);
                skipped++;
            }
        }

        return (seeded, skipped, excluded);
    }

    private static string ResolvePackageBase(SearchIndexData index, string packageName)
    {
        if (index.ByNames.TryGetValue(packageName, out var metadata) && !string.IsNullOrEmpty(metadata.PackageBase))
            return metadata.PackageBase;

        return packageName;
    }
}