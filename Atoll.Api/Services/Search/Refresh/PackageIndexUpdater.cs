using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Atoll.Api.Extensions;
using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Search.Refresh;

public sealed class PackageIndexUpdater(
    PackageIndexStore store,
    IAurMetadataRepository aurMetadataRepository,
    IHttpClientFactory httpClientFactory,
    IOptions<AtollOptions> options,
    ILogger<PackageIndexUpdater> logger,
    UpstreamPackageReconciler? reconciler = null)
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
        var next = PackageDataLoader.BuildFromPackages(packages);
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
            logger.LogDebug("Fetching updated package data from AUR.");

            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.DataSource.DataFileUrl);
            if (_etag is not null)
                request.Headers.IfNoneMatch.Add(_etag);
            if (_lastModified is not null)
                request.Headers.IfModifiedSince = _lastModified;

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var current = store.Current;
                // A 304 while a suspicious shrink awaits confirmation means the archive re-served
                // the old artifact, so the snapshot in hand is not the promised confirmation
                // download and must not drive pruning.
                if (reconciler is not null && current.ByNames.Count > 0 && !_pruneConfirmationPending)
                    await reconciler.ReconcileAsync([.. current.ByNames.Keys], current.ByNames.Count, cancellationToken);

                RecordSuccess();
                logger.LogDebug("AUR package metadata has not changed.");
                return true;
            }

            response.EnsureSuccessStatusCode();
            await using var compressed = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

            var packages = await ParsePackagesAsync(gzip, cancellationToken);
            if (packages.Count == 0)
                throw new InvalidDataException("AUR package dump contained no packages.");

            logger.LogDebug("Parsed {PackageCount} packages from the AUR metadata dump.", packages.Count);

            var previousPackageCount = store.Current.ByNames.Count;
            var pruneNeedsConfirmation = reconciler is not null
                                         && options.Value.DataSource.PruneDeletedPackages
                                         && UpstreamPackageReconciler.IsSuspiciousShrink(packages.Count, previousPackageCount);
            await aurMetadataRepository.SaveAsync(packages, cancellationToken);

            var next = PackageDataLoader.BuildFromPackages(packages);
            store.Replace(next);

            // Keep the old validators while a suspicious shrink awaits confirmation. This forces
            // one confirmation download even if the archive would otherwise answer 304 next cycle.
            if (!pruneNeedsConfirmation)
            {
                _etag = response.Headers.ETag ?? _etag;
                _lastModified = response.Content.Headers.LastModified ?? _lastModified;
            }
            _pruneConfirmationPending = pruneNeedsConfirmation;

            if (reconciler is not null)
            {
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

    private static async Task<IReadOnlyList<AurPackageMetadata>> ParsePackagesAsync(
        Stream gzipStream,
        CancellationToken ct)
    {
        // The whole decompressed dump is held in memory (~110k packages today).
        // If the dump grows significantly, switch to Utf8JsonReader / DeserializeAsyncEnumerable.
        using var doc = await JsonDocument.ParseAsync(gzipStream, cancellationToken: ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AUR package dump is not a JSON array.");

        var packages = new List<AurPackageMetadata>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            packages.Add(element.DeserializeAurPackage());
        }

        return packages;
    }
}