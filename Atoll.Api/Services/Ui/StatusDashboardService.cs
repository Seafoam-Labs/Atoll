using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Ui;

/// <summary>
///     Point-in-time read-only view over the status stores and a few repository counts. The values
///     come from separate sources and may be milliseconds apart; the page labels itself with the
///     assembly time instead of implying a consistent snapshot. Assembled models are cached briefly
///     so repeated page loads or polling do not re-query MongoDB on every hit.
/// </summary>
public sealed record StatusDashboardModel(
    RefreshStatusSnapshot IndexRefresh,
    int IndexPackages,
    int SeededPackages,
    long PendingScans,
    SecurityScanStatusSnapshot Security,
    HeadScanStatusCounts SecurityHeadCounts,
    int ExcludedPackageBases,
    IReadOnlyList<string> ExcludedPackageBaseNames,
    SeedMode SeedMode,
    DirectSeedStatusSnapshot DirectSeed,
    BulkSeedStatusSnapshot BulkSeed,
    PackageRefreshStatusSnapshot Refresh,
    DateTimeOffset AssembledUtc);

public sealed class StatusDashboardService(
    PackageIndexStore indexStore,
    PackageIndexUpdater indexUpdater,
    IPackageService packageService,
    IPackageSecurityRepository securityRepository,
    ISeedExclusionRepository seedExclusions,
    SecurityScanStatusStore securityStatus,
    DirectSeedStatusStore directSeedStatus,
    BulkSeedStatusStore bulkSeedStatus,
    RefreshStatusStore refreshStatus,
    IOptions<AtollOptions> options)
{
    public const int ExclusionRenderCap = 50;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private StatusDashboardModel? _cached;

    public async Task<StatusDashboardModel> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var cached = Volatile.Read(ref _cached);
        if (cached is not null && IsFresh(cached)) return cached;

        await _cacheGate.WaitAsync(ct);
        try
        {
            cached = Volatile.Read(ref _cached);
            if (cached is not null && IsFresh(cached)) return cached;

            var model = await AssembleAsync(ct);
            Volatile.Write(ref _cached, model);
            return model;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static bool IsFresh(StatusDashboardModel model)
    {
        return DateTimeOffset.UtcNow - model.AssembledUtc < CacheTtl;
    }

    private async Task<StatusDashboardModel> AssembleAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var indexRefresh = indexUpdater.GetStatus();
        var indexPackages = indexStore.Current.ByNames.Count;

        // Independent read-only queries over different collections; run them concurrently
        // instead of paying four sequential round trips.
        var seededTask = packageService.CountAsync();
        var pendingTask = securityRepository.CountPendingAsync(ct);
        var headCountsTask = securityRepository.CountHeadStatusesAsync(ct);
        var excludedTask = seedExclusions.ListDocumentTooLargePackageBasesAsync(ct);
        await Task.WhenAll(seededTask, pendingTask, headCountsTask, excludedTask);

        var excludedNames = (await excludedTask).Order(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> excludedRendered = excludedNames.Count > ExclusionRenderCap
            ? [.. excludedNames.Take(ExclusionRenderCap)]
            : excludedNames;

        return new StatusDashboardModel(
            indexRefresh,
            indexPackages,
            await seededTask,
            await pendingTask,
            securityStatus.GetSnapshot(),
            await headCountsTask,
            excludedNames.Count,
            excludedRendered,
            options.Value.Seed.Mode,
            directSeedStatus.GetSnapshot(),
            bulkSeedStatus.GetSnapshot(),
            refreshStatus.GetSnapshot(),
            DateTimeOffset.UtcNow);
    }
}
