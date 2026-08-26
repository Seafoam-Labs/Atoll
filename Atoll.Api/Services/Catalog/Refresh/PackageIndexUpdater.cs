using System.Net.Http.Headers;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Catalog.Refresh;

public sealed class PackageIndexUpdater(
    PackageIndexStore store,
    IAurMetadataRepository aurMetadataRepository,
    AurMetadataClient metadataClient,
    IOptions<AtollOptions> options,
    ILogger<PackageIndexUpdater> logger,
    UpstreamPackageReconciler reconciler)
{
    private readonly Lock _timeLock = new();

    private EntityTagHeaderValue? _etag;
    private DateTimeOffset? _lastModified;
    private bool _pruneConfirmationPending;
    private long _attempts;
    private long _failures;
    private DateTimeOffset? _lastFailedUtc;
    private DateTimeOffset? _lastLoadedFromCacheUtc;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset? _lastSucceededUtc;
    private long _successes;

    private string MetadataCollection => options.Value.Mongo.Collections.AurMetadata;

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Max(1, options.Value.DataSource.RefreshIntervalMinutes));

    public RefreshStatusSnapshot GetStatus()
    {
        lock (_timeLock)
        {
            return new RefreshStatusSnapshot(
                MetadataCollection,
                RefreshInterval,
                Interlocked.Read(ref _attempts),
                Interlocked.Read(ref _successes),
                Interlocked.Read(ref _failures),
                _lastStartedUtc,
                _lastSucceededUtc,
                _lastFailedUtc,
                _lastLoadedFromCacheUtc);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var packages = await aurMetadataRepository.LoadAsync(cancellationToken);
        if (packages.Count == 0)
        {
            logger.LogWarning("No cached package metadata. The API starts with empty indexes.");
            return;
        }

        logger.LogInformation("Loaded {Count} packages. Building indexes.", packages.Count);
        var next = PackageIndexBuilder.BuildFromPackages(packages);
        store.Replace(next);
        lock (_timeLock)
        {
            _lastLoadedFromCacheUtc = DateTimeOffset.UtcNow;
        }
    }

    public async Task<bool> DownloadAndReloadAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        lock (_timeLock)
        {
            _lastStartedUtc = DateTimeOffset.UtcNow;
        }

        try
        {
            var result = await metadataClient.FetchAsync(_etag, _lastModified, cancellationToken);

            if (result is AurMetadataResult.NotModified)
            {
                var current = store.Current;
                // A 304 while a suspicious shrink awaits confirmation means the archive re-served
                // the old artifact, so the snapshot in hand is not the promised confirmation
                // download and must not drive pruning.
                if (current.ByNames.Count > 0 && !_pruneConfirmationPending)
                    await reconciler.ReconcileAsync([.. current.ByNames.Keys], current.ByNames.Count, cancellationToken);

                RecordSuccess();
                logger.LogDebug("AUR package metadata has not changed.");
                return true;
            }

            var snapshot = (AurMetadataResult.Snapshot)result;
            var packages = snapshot.Packages;

            logger.LogDebug("Parsed {PackageCount} packages from the AUR metadata dump.", packages.Count);

            var previousPackageCount = store.Current.ByNames.Count;
            var pruneNeedsConfirmation = options.Value.DataSource.PruneDeletedPackages
                                         && UpstreamPackageReconciler.IsSuspiciousShrink(packages.Count, previousPackageCount);
            await aurMetadataRepository.SaveAsync(packages, cancellationToken);

            var next = PackageIndexBuilder.BuildFromPackages(packages);
            store.Replace(next);

            // Keep the old validators while a suspicious shrink awaits confirmation. This forces
            // one confirmation download even if the archive would otherwise answer 304 next cycle.
            if (!pruneNeedsConfirmation)
            {
                _etag = snapshot.ETag ?? _etag;
                _lastModified = snapshot.LastModified ?? _lastModified;
            }
            _pruneConfirmationPending = pruneNeedsConfirmation;

            try
            {
                await reconciler.ReconcileAsync([.. next.ByNames.Keys], previousPackageCount, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A prune failure must not mask the successful refresh above; the next cycle
                // reconciles again against the same snapshot.
                logger.LogWarning(ex, "Package index refreshed, but upstream reconciliation failed.");
            }

            logger.LogInformation("Package index refreshed with {PackageCount} packages.", packages.Count);

            RecordSuccess();
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            lock (_timeLock)
            {
                _lastFailedUtc = DateTimeOffset.UtcNow;
            }

            logger.LogWarning(ex, "Unable to fetch and store new package data.");
            return false;
        }
    }

    private void RecordSuccess()
    {
        Interlocked.Increment(ref _successes);
        lock (_timeLock)
        {
            _lastSucceededUtc = DateTimeOffset.UtcNow;
        }
    }
}
