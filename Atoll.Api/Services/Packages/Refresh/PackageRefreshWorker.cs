using System.Diagnostics;
using System.Threading.Channels;
using Atoll.Api.Services.Packages.Mirror;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Packages.Refresh;

public sealed class PackageRefreshWorker(
    PackageIndexStore indexStore,
    IPackageRepository repo,
    IPackageService packageService,
    IAurMirror mirror,
    RefreshStatusStore status,
    IOptions<AtollOptions> options,
    ILogger<PackageRefreshWorker> logger) : BackgroundService
{
    private readonly TimeSpan _maxStaleness = TimeSpan.FromHours(Math.Max(1, options.Value.Refresh.MaxStalenessHours));
    private readonly RefreshOptions _options = options.Value.Refresh;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(1);
        var parallelism = Math.Clamp(_options.Parallelism, 1, 128);

        logger.LogInformation(
            "Package refresh started with a {Interval} interval, a {MaxStaleness} staleness threshold, and parallelism {Parallelism}.",
            interval, _maxStaleness, parallelism);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refresh cycle failed; will retry after backoff.");
                status.RecordCycleFailed();
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                continue;
            }

            await Task.Delay(interval, stoppingToken);
        }

        logger.LogInformation("Package refresh stopped.");
    }

    internal async Task<RefreshCycleOutcome> RunCycleAsync(CancellationToken stoppingToken)
    {
        var index = indexStore.Current;
        if (index.ByNames.Count == 0)
        {
            logger.LogDebug("Package index is empty; skipping refresh cycle.");
            return RefreshCycleOutcome.BackedOff;
        }

        var batchSize = Math.Clamp(_options.BatchSize, 10, 10_000);
        var batchDelay = TimeSpan.FromMilliseconds(Math.Max(100, _options.BatchDelayMs));
        var maxPackagesPerRun = Math.Max(1, _options.MaxPackagesPerRun);
        var parallelism = Math.Clamp(_options.Parallelism, 1, 128);

        status.BeginCycle();
        var cycleWatch = Stopwatch.StartNew();

        var packagesUpdated = 0;
        var packagesUnchanged = 0;
        var packagesSkipped = 0;

        try
        {
            await mirror.EnsureInitializedAsync(stoppingToken);

            var states = await repo.ListSyncStatesAsync(stoppingToken);
            if (states.Count == 0)
            {
                logger.LogDebug("No seeded packages to refresh.");
                return RefreshCycleOutcome.BackedOff;
            }

            var grouped = RefreshPlan.GroupByPackageBase(states, index);
            var branchHeads = await mirror.ListBranchHeadsAsync(stoppingToken);

            var refsMissing = grouped.Keys.Count(pkgBase => !branchHeads.ContainsKey(pkgBase));
            if (refsMissing > 0)
                status.AddRefsSkipped(refsMissing);

            var now = DateTimeOffset.UtcNow;
            var candidates = RefreshPlan.SelectCandidates(grouped, branchHeads, now, _maxStaleness);

            var ordered = candidates
                .OrderBy(c => c.Members.Min(m => m.LastSyncSucceededAt ?? DateTimeOffset.MinValue))
                .ToList();

            var noFetch = new List<CandidatePackageBase>();
            var needsFetch = new List<CandidatePackageBase>();
            foreach (var candidate in ordered)
                if (candidate.HeadUnchanged)
                    noFetch.Add(candidate);
                else
                    needsFetch.Add(candidate);

            var fetchSelected = new List<CandidatePackageBase>();
            var covered = 0;
            foreach (var candidate in needsFetch.TakeWhile(_ => covered < maxPackagesPerRun))
            {
                fetchSelected.Add(candidate);
                covered += candidate.Members.Count;
            }

            var skippedDueToCap = needsFetch.Count - fetchSelected.Count;
            if (skippedDueToCap > 0)
                logger.LogDebug(
                    "Refresh capped at {MaxPackagesPerRun} packages; {SkippedPkgBaseCount} pkgbases deferred to the next cycle.",
                    maxPackagesPerRun, skippedDueToCap);

            await Parallel.ForEachAsync(
                noFetch,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = stoppingToken },
                async (candidate, token) =>
                {
                    await repo.UpdateSyncStateAsync(candidate.Members.Select(m => m.PackageName).ToList(), candidate.UpstreamHead, true,
                        null, token);
                    Interlocked.Add(ref packagesUnchanged, candidate.Members.Count);
                });

            status.AddCandidatePackages(fetchSelected.Sum(c => c.Members.Count));
            status.AddCandidatePackageBases(fetchSelected.Count);

            logger.LogInformation(
                "Refresh cycle: {SeededPackageCount} seeded packages across {PkgBaseCount} pkgbases; {CandidatePkgBaseCount} candidate pkgbases, {SelectedPkgBaseCount} selected for fetch, {NoFetchCount} already up-to-date.",
                states.Count, grouped.Count, candidates.Count, fetchSelected.Count, noFetch.Count);

            long fetchTicks = 0;
            long seedTicks = 0;

            // Fetching is network-bound while archive extraction and revision appending are
            // CPU/DB-bound, so they run as a two-stage pipeline.
            var pipeline = Channel.CreateBounded<FetchedRefreshBatch>(new BoundedChannelOptions(2)
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
                        Interlocked.Add(ref packagesSkipped, envelope.Batch.Sum(c => c.Members.Count));
                        status.AddRefsFailed(envelope.Batch.Count);
                        await RecordBatchFailureAsync(envelope.Batch, envelope.Failure.Message, stoppingToken);
                        continue;
                    }

                    var result = envelope.Result!;
                    status.AddRefsFailed(result.Failed.Count);

                    var failedBases = new HashSet<string>(result.Failed, StringComparer.Ordinal);
                    foreach (var candidate in envelope.Batch)
                    {
                        if (!failedBases.Contains(candidate.PackageBase)) continue;

                        Interlocked.Add(ref packagesSkipped, candidate.Members.Count);
                        await repo.UpdateSyncStateAsync(candidate.Members.Select(m => m.PackageName).ToList(), null, false, "fetch failed",
                            stoppingToken);
                    }

                    var succeededBases = new HashSet<string>(result.Succeeded, StringComparer.Ordinal);
                    var refreshable = envelope.Batch.Where(c => succeededBases.Contains(c.PackageBase)).ToList();

                    var seedWatch = Stopwatch.StartNew();
                    var (updated, unchanged, skipped) = await RefreshFetchedCandidatesAsync(refreshable, parallelism, stoppingToken);
                    Interlocked.Add(ref seedTicks, seedWatch.ElapsedTicks);

                    Interlocked.Add(ref packagesUpdated, updated);
                    Interlocked.Add(ref packagesUnchanged, unchanged);
                    Interlocked.Add(ref packagesSkipped, skipped);

                    logger.LogDebug(
                        "Refresh batch complete: {FetchedPackageBaseCount} fetched, {FailedPackageBaseCount} failed, {UpdatedSoFar} updated so far.",
                        result.Succeeded.Count, result.Failed.Count, packagesUpdated);
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
                "Refresh cycle complete in {ElapsedMs} ms ({FetchMs} ms fetching, {SeedMs} ms applying; phases overlap): " +
                "{Updated} updated, {Unchanged} unchanged, {Skipped} skipped.",
                cycleWatch.ElapsedMilliseconds, TimeSpan.FromTicks(fetchTicks).TotalMilliseconds,
                TimeSpan.FromTicks(seedTicks).TotalMilliseconds, packagesUpdated, packagesUnchanged, packagesSkipped);

            status.AddPackagesUpdated(packagesUpdated);
            status.AddPackagesUnchanged(packagesUnchanged);
            status.AddPackagesSkipped(packagesSkipped);

            return RefreshCycleOutcome.Completed;

            async Task ProduceAsync(CancellationToken produceToken)
            {
                try
                {
                    foreach (var batch in BulkSeedPlan.ChunkBy(fetchSelected, batchSize))
                    {
                        if (produceToken.IsCancellationRequested) break;

                        var fetchWatch = Stopwatch.StartNew();
                        FetchedRefreshBatch envelope;
                        try
                        {
                            var result = await mirror.FetchAsync(batch.Select(c => c.PackageBase).ToList(), produceToken);
                            envelope = new FetchedRefreshBatch(batch, result, null);
                        }
                        catch (OperationCanceledException) when (produceToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            envelope = new FetchedRefreshBatch(batch, null, ex);
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
            status.EndCycle();
        }
    }

    private async Task<(int updated, int unchanged, int skipped)> RefreshFetchedCandidatesAsync(
        IReadOnlyList<CandidatePackageBase> candidates,
        int parallelism,
        CancellationToken ct)
    {
        var updated = 0;
        var unchanged = 0;
        var skipped = 0;

        // Archive extraction is a read-only git operation on the shared bare cache, and each
        // candidate touches a disjoint set of package documents, so they refresh concurrently.
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (candidate, token) =>
            {
                IReadOnlyDictionary<string, string> files;
                try
                {
                    files = await mirror.ReadFilesAsync(candidate.PackageBase, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not read files for pkgbase {PkgBase}; skipping.", candidate.PackageBase);
                    Interlocked.Add(ref skipped, candidate.Members.Count);
                    await repo.UpdateSyncStateAsync(candidate.Members.Select(m => m.PackageName).ToList(), null, false, ex.Message, token);
                    return;
                }

                var succeededMembers = new List<string>(candidate.Members.Count);
                foreach (var packageName in candidate.Members.Select(m => m.PackageName))
                    try
                    {
                        var changed = await packageService.AppendRevisionFromUpstreamAsync(packageName, files, token);

                        if (changed)
                        {
                            Interlocked.Increment(ref updated);
                            logger.LogTrace("Refreshed package {PackageName} from upstream.", packageName);
                        }
                        else
                        {
                            Interlocked.Increment(ref unchanged);
                        }

                        succeededMembers.Add(packageName);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to refresh {PackageName}.", packageName);
                        Interlocked.Increment(ref skipped);
                        await repo.UpdateSyncStateAsync([packageName], null, false, ex.Message, token);
                    }

                if (succeededMembers.Count > 0)
                    await repo.UpdateSyncStateAsync(succeededMembers, candidate.UpstreamHead, true, null, token);
            });

        return (updated, unchanged, skipped);
    }

    private async Task RecordBatchFailureAsync(IReadOnlyList<CandidatePackageBase> batch, string error, CancellationToken ct)
    {
        foreach (var candidate in batch)
            await repo.UpdateSyncStateAsync(candidate.Members.Select(m => m.PackageName).ToList(), null, false, error, ct);
    }

    private sealed record FetchedRefreshBatch(
        IReadOnlyList<CandidatePackageBase> Batch,
        BulkFetchResult? Result,
        Exception? Failure);
}

internal enum RefreshCycleOutcome
{
    Completed,
    BackedOff
}