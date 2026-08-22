using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Refresh;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Services.Security;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Ui;

/// <summary>
///     Point-in-time read-only view over the status stores and a few repository counts. The values
///     come from separate sources and may be milliseconds apart; the page labels itself
///     "last rendered" instead of implying a consistent snapshot.
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
    PackageRefreshStatusSnapshot Refresh);

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

    public async Task<StatusDashboardModel> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var indexRefresh = indexUpdater.GetStatus();
        var indexPackages = indexStore.Current.ByNames.Count;

        var seededPackages = await packageService.ListAsync();
        var pendingScans = await securityRepository.CountPendingAsync(ct);
        var security = securityStatus.GetSnapshot();
        var securityHeadCounts = await securityRepository.CountHeadStatusesAsync(ct);
        var excluded = await seedExclusions.ListDocumentTooLargePackageBasesAsync(ct);

        var excludedNames = excluded.Order(StringComparer.Ordinal).ToList();
        IReadOnlyList<string> excludedRendered = excludedNames.Count > ExclusionRenderCap
            ? [.. excludedNames.Take(ExclusionRenderCap)]
            : excludedNames;

        return new StatusDashboardModel(
            indexRefresh,
            indexPackages,
            seededPackages.Count,
            pendingScans,
            security,
            securityHeadCounts,
            excluded.Count,
            excludedRendered,
            options.Value.Seed.Mode,
            directSeedStatus.GetSnapshot(),
            bulkSeedStatus.GetSnapshot(),
            refreshStatus.GetSnapshot());
    }
}
