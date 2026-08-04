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

        logger.LogInformation("Package refresh started with a {Interval} interval and a {MaxStaleness} staleness threshold.",
            interval, _maxStaleness);

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

        status.BeginCycle();

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

            foreach (var candidate in noFetch)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await repo.UpdateSyncStateAsync(
                    candidate.Members.Select(m => m.PackageName).ToList(),
                    candidate.UpstreamHead,
                    true,
                    null,
                    stoppingToken);
                packagesUnchanged += candidate.Members.Count;
            }

            status.AddCandidatePackages(fetchSelected.Sum(c => c.Members.Count));
            status.AddCandidatePackageBases(fetchSelected.Count);

            logger.LogInformation(
                "Refresh cycle: {SeededPackageCount} seeded packages across {PkgBaseCount} pkgbases; {CandidatePkgBaseCount} candidate pkgbases, {SelectedPkgBaseCount} selected for fetch, {NoFetchCount} already up-to-date.",
                states.Count, grouped.Count, candidates.Count, fetchSelected.Count, noFetch.Count);

            foreach (var batch in BulkSeedPlan.ChunkBy(fetchSelected, batchSize))
            {
                if (stoppingToken.IsCancellationRequested) break;

                var updatedSoFar = 0;
                BulkFetchResult result;
                try
                {
                    result = await mirror.FetchAsync(
                        batch.Select(c => c.PackageBase).ToList(),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Batch fetch of {Count} pkgbases failed entirely; skipping batch.", batch.Count);
                    packagesSkipped += batch.Sum(c => c.Members.Count);
                    status.AddRefsFailed(batch.Count);
                    await RecordBatchFailureAsync(batch, ex.Message, stoppingToken);
                    await Task.Delay(batchDelay, stoppingToken);
                    continue;
                }

                status.AddRefsFailed(result.Failed.Count);

                var succeededByBase = batch.ToDictionary(c => c.PackageBase, StringComparer.Ordinal);
                foreach (var failedBase in result.Failed)
                    if (succeededByBase.TryGetValue(failedBase, out var candidate))
                    {
                        packagesSkipped += candidate.Members.Count;
                        await repo.UpdateSyncStateAsync(
                            candidate.Members.Select(m => m.PackageName).ToList(),
                            null,
                            false,
                            "fetch failed",
                            stoppingToken);
                    }

                foreach (var candidate in batch.Where(c => result.Succeeded.Contains(c.PackageBase)))
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    IReadOnlyDictionary<string, string> files;
                    try
                    {
                        files = await mirror.ReadFilesAsync(candidate.PackageBase, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not read files for pkgbase {PkgBase}; skipping.", candidate.PackageBase);
                        packagesSkipped += candidate.Members.Count;
                        await repo.UpdateSyncStateAsync(
                            candidate.Members.Select(m => m.PackageName).ToList(),
                            null,
                            false,
                            ex.Message,
                            stoppingToken);
                        continue;
                    }

                    var succeededMembers = new List<string>(candidate.Members.Count);
                    foreach (var packageName in candidate.Members.Select(m => m.PackageName))
                        try
                        {
                            var changed = await packageService.AppendRevisionFromUpstreamAsync(packageName, files, stoppingToken);

                            if (changed)
                            {
                                updatedSoFar++;
                                logger.LogTrace("Refreshed package {PackageName} from upstream.", packageName);
                            }
                            else
                            {
                                packagesUnchanged++;
                            }

                            succeededMembers.Add(packageName);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to refresh {PackageName}.", packageName);
                            packagesSkipped++;
                            await repo.UpdateSyncStateAsync(
                                [packageName],
                                null,
                                false,
                                ex.Message,
                                stoppingToken);
                        }

                    if (succeededMembers.Count > 0)
                        await repo.UpdateSyncStateAsync(
                            succeededMembers,
                            candidate.UpstreamHead,
                            true,
                            null,
                            stoppingToken);
                }

                packagesUpdated += updatedSoFar;

                logger.LogDebug(
                    "Refresh batch complete: {FetchedPackageBaseCount} fetched, {FailedPackageBaseCount} failed, {UpdatedSoFar} updated so far.",
                    result.Succeeded.Count, result.Failed.Count, updatedSoFar);

                await Task.Delay(batchDelay, stoppingToken);
            }

            logger.LogInformation(
                "Refresh cycle complete: {Updated} updated, {Unchanged} unchanged, {Skipped} skipped.",
                packagesUpdated, packagesUnchanged, packagesSkipped);

            status.AddPackagesUpdated(packagesUpdated);
            status.AddPackagesUnchanged(packagesUnchanged);
            status.AddPackagesSkipped(packagesSkipped);

            return RefreshCycleOutcome.Completed;
        }
        finally
        {
            status.EndCycle();
        }
    }

    private async Task RecordBatchFailureAsync(IReadOnlyList<CandidatePackageBase> batch, string error, CancellationToken ct)
    {
        foreach (var candidate in batch)
            await repo.UpdateSyncStateAsync(
                candidate.Members.Select(m => m.PackageName).ToList(),
                null,
                false,
                error,
                ct);
    }
}

internal enum RefreshCycleOutcome
{
    Completed,
    BackedOff
}