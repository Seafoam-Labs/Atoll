using System.IO.Compression;
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
    ILogger<PackageIndexUpdater> logger)
{
    private readonly Lock _timeLock = new();

    private long _attempts;
    private long _failures;
    private DateTimeOffset? _lastFailedUtc;
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
                _lastFailedUtc);
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
            logger.LogInformation("Fetching updated package data from AUR.");

            var client = httpClientFactory.CreateClient();
            await using var compressed = await client.GetStreamAsync(
                options.Value.DataSource.DataFileUrl, cancellationToken);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

            var packages = await ParsePackagesAsync(gzip, cancellationToken);
            logger.LogInformation("Parsed {Count} packages.", packages.Count);

            await aurMetadataRepository.SaveAsync(packages, cancellationToken);
            logger.LogInformation("Stored {Count} packages.", packages.Count);

            var next = PackageDataLoader.BuildFromPackages(packages);
            store.Replace(next);

            Interlocked.Increment(ref _successes);
            lock (_timeLock)
            {
                _lastSucceededUtc = DateTimeOffset.UtcNow;
            }

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