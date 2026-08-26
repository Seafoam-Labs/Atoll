using Atoll.Api.Services.Packages;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Catalog.Refresh;

public sealed class UpstreamPackageReconciler(
    IPackageService packages,
    IOptions<AtollOptions> options,
    ILogger<UpstreamPackageReconciler> logger)
{
    private readonly bool _pruneDeletedPackages = options.Value.DataSource.PruneDeletedPackages;

    /// <summary>
    ///     Shared by <see cref="ReconcileAsync" /> and the index updater's confirmation gate:
    ///     both sides of the "defer one cycle on a sudden shrink" mechanism must agree on what
    ///     counts as suspicious, or the confirmation download and the deferral fall out of sync.
    /// </summary>
    public static bool IsSuspiciousShrink(int currentCount, int previousCount)
    {
        return previousCount > 0 && currentCount < previousCount * 0.9;
    }

    public async Task<int> ReconcileAsync(
        IReadOnlyCollection<string> upstreamPackageNames,
        int previousUpstreamPackageCount,
        CancellationToken ct)
    {
        if (!_pruneDeletedPackages)
            return 0;

        if (upstreamPackageNames.Count == 0)
            throw new InvalidDataException("Refusing to prune packages from an empty upstream snapshot.");

        // Treat a sudden >10% shrink as suspect for one cycle. The index is still updated, so an
        // identical complete snapshot on the next poll confirms the change and allows pruning.
        if (IsSuspiciousShrink(upstreamPackageNames.Count, previousUpstreamPackageCount))
        {
            logger.LogWarning(
                "Skipping package pruning because the AUR snapshot shrank from {PreviousCount} to {CurrentCount}; a subsequent consistent snapshot can confirm the change.",
                previousUpstreamPackageCount, upstreamPackageNames.Count);
            return 0;
        }

        var upstream = upstreamPackageNames.ToHashSet(StringComparer.Ordinal);
        var local = await packages.ListAsync();
        var deleted = local
            .Where(name => !upstream.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToList();

        foreach (var packageName in deleted)
        {
            ct.ThrowIfCancellationRequested();
            await packages.DeleteAsync(packageName, ct);
            logger.LogInformation("Deleted package {PackageName} because it is no longer present in the AUR metadata snapshot.",
                packageName);
        }

        if (deleted.Count > 0)
            logger.LogInformation("Upstream reconciliation deleted {DeletedPackageCount} packages.", deleted.Count);

        return deleted.Count;
    }
}
