using System.Diagnostics;
using System.Threading.Channels;
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
        var parallelism = Math.Clamp(_options.Parallelism, 1, 128);

        logger.LogInformation(
            "Bulk package seeding started with a batch size of {BatchSize}, a {BatchDelay} batch delay, parallelism {Parallelism}, and direct AUR fallback {AurFallbackEnabled}.",
            batchSize, batchDelay, parallelism, _options.AurFallbackForNotOnMirror ? "enabled" : "disabled");

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
        var cycleWatch = Stopwatch.StartNew();

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

            var parallelism = Math.Clamp(_options.Parallelism, 1, 128);
            long fetchTicks = 0;
            long seedTicks = 0;

            // Fetching is network-bound while archive extraction and seeding are CPU/DB-bound, so
            // they run as a two-stage pipeline: later batches are fetched while earlier ones seed.
            var pipeline = Channel.CreateBounded<FetchedBatch>(new BoundedChannelOptions(2)
            {
                SingleWriter = true,
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var producer = ProduceAsync(producerCts.Token);

            try
            {
                await foreach (var envelope in pipeline.Reader.ReadAllAsync(stoppingToken))
                {
                    if (envelope.Failure is not null)
                    {
                        status.RecordBatchFailed();
                        Interlocked.Add(ref packagesSkipped, envelope.Batch.Sum(b => targets[b].Count));
                        continue;
                    }

                    var result = envelope.Result!;
                    status.RecordBatchSucceeded();
                    status.AddRefsFailed(result.Failed.Count);
                    Interlocked.Add(ref packagesSkipped, result.Failed.Sum(b => targets[b].Count));

                    var seedWatch = Stopwatch.StartNew();
                    var (batchSeeded, batchSkipped, batchExcluded) =
                        await SeedFetchedBasesAsync(result.Succeeded, targets, parallelism, stoppingToken);
                    Interlocked.Add(ref seedTicks, seedWatch.ElapsedTicks);

                    Interlocked.Add(ref packagesSeeded, batchSeeded);
                    Interlocked.Add(ref packagesSkipped, batchSkipped);
                    Interlocked.Add(ref packagesExcluded, batchExcluded);

                    logger.LogDebug(
                        "Bulk seed batch complete: {FetchedPackageBaseCount} pkgbases fetched, {FailedPackageBaseCount} failed, {PackagesSeededSoFar} packages seeded so far.",
                        result.Succeeded.Count, result.Failed.Count, packagesSeeded);
                }
            }
            finally
            {
                await producerCts.CancelAsync();
                try
                {
                    await producer;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Normal shutdown raced with an in-flight fetch.
                }
            }

            logger.LogInformation(
                "Bulk seed cycle complete in {ElapsedMs} ms ({FetchMs} ms fetching, {SeedMs} ms seeding; phases overlap): " +
                "{SeededPackageCount} seeded, {SkippedPackageCount} skipped, {ExcludedPackageCount} excluded.",
                cycleWatch.ElapsedMilliseconds, TimeSpan.FromTicks(fetchTicks).TotalMilliseconds,
                TimeSpan.FromTicks(seedTicks).TotalMilliseconds, packagesSeeded, packagesSkipped, packagesExcluded);

            return (packagesSeeded, packagesSkipped, backedOff: false);

            async Task ProduceAsync(CancellationToken produceToken)
            {
                try
                {
                    foreach (var batch in BulkSeedPlan.ChunkBy(fetchable, batchSize))
                    {
                        if (produceToken.IsCancellationRequested) break;

                        status.RecordBatchAttempted();

                        var fetchWatch = Stopwatch.StartNew();
                        FetchedBatch envelope;
                        try
                        {
                            var result = await mirror.FetchAsync(batch, produceToken);
                            envelope = new FetchedBatch(batch, result, null);
                        }
                        catch (OperationCanceledException) when (produceToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            envelope = new FetchedBatch(batch, null, ex);
                            logger.LogWarning(ex, "Batch fetch of {Count} pkgbases failed entirely; skipping batch.", batch.Count);
                        }

                        Interlocked.Add(ref fetchTicks, fetchWatch.ElapsedTicks);
                        await pipeline.Writer.WriteAsync(envelope, produceToken);
                        await Task.Delay(batchDelay, produceToken);
                    }
                }
                catch (OperationCanceledException) when (produceToken.IsCancellationRequested)
                {
                    // Shutdown, or the consumer stopped early; stop producing.
                }
                finally
                {
                    pipeline.Writer.TryComplete();
                }
            }
        }
        finally
        {
            status.AddPackagesSeeded(packagesSeeded);
            status.AddPackagesSkipped(packagesSkipped);
            status.AddPackagesExcluded(packagesExcluded);
            status.EndCycle();
        }
    }

    private async Task<(int seeded, int skipped, int excluded)> SeedFetchedBasesAsync(
        IReadOnlyList<string> pkgBases,
        IReadOnlyDictionary<string, IReadOnlyList<string>> targets,
        int parallelism,
        CancellationToken ct)
    {
        var seeded = 0;
        var skipped = 0;
        var excluded = 0;

        // Archive extraction is a read-only git operation on the shared bare cache, and seeding
        // writes distinct package documents, so pkgbases are processed concurrently.
        await Parallel.ForEachAsync(
            pkgBases,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (pkgBase, token) =>
            {
                IReadOnlyDictionary<string, string> files;
                try
                {
                    files = await mirror.ReadFilesAsync(pkgBase, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read files for pkgbase {PkgBase}; skipping.", pkgBase);
                    Interlocked.Add(ref skipped, targets[pkgBase].Count);
                    return;
                }

                foreach (var packageName in targets[pkgBase])
                    try
                    {
                        await packageService.SeedFilesAsync(packageName, files);
                        Interlocked.Increment(ref seeded);
                    }
                    catch (PackageConflictException)
                    {
                        // Race: seeded between list and seed. Not an error.
                    }
                    catch (PackageDocumentTooLargeException ex)
                    {
                        await exclusions.RecordDocumentTooLargeAsync(pkgBase, targets[pkgBase], ex.SerializedSizeBytes, token);
                        Interlocked.Add(ref excluded, targets[pkgBase].Count);
                        logger.LogWarning(ex,
                            "Excluded pkgbase {PkgBase} from future bulk seed cycles because its {SizeBytes}-byte package document exceeds MongoDB's limit.",
                            pkgBase, ex.SerializedSizeBytes);
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to seed {PackageName}.", packageName);
                        Interlocked.Increment(ref skipped);
                    }
            });

        return (seeded, skipped, excluded);
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

    private sealed record FetchedBatch(
        IReadOnlyList<string> Batch,
        BulkFetchResult? Result,
        Exception? Failure);
}