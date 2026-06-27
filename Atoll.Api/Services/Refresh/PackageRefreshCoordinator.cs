using System.IO.Compression;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Refresh;

public sealed class PackageRefreshCoordinator(
    PackageIndexStore store,
    IHttpClientFactory httpClientFactory,
    IOptions<AtollOptions> options,
    ILogger<PackageRefreshCoordinator> logger)
{
    private readonly Lock _timeLock = new();

    private long _attempts;
    private long _failures;
    private DateTimeOffset? _lastFailedUtc;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset? _lastSucceededUtc;
    private long _successes;

    public string DataFilePath => options.Value.DataSource.DataFile;

    public TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Max(1, options.Value.DataSource.RefreshIntervalMinutes));

    public RefreshStatusSnapshot GetStatus()
    {
        lock (_timeLock)
        {
            return new RefreshStatusSnapshot(
                DataFilePath,
                RefreshInterval,
                Interlocked.Read(ref _attempts),
                Interlocked.Read(ref _successes),
                Interlocked.Read(ref _failures),
                _lastStartedUtc,
                _lastSucceededUtc,
                _lastFailedUtc);
        }
    }

    public async Task InitializeFromDiskAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(DataFilePath))
        {
            logger.LogWarning("No local package data found at {Path}. The API starts with empty indexes.",
                DataFilePath);
            return;
        }

        await ReloadFromDiskAsync(cancellationToken);
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
            logger.LogInformation("Fetching updated package data.");
            await DownloadPackageDataAsync(cancellationToken);

            logger.LogInformation("Reforming package indices...");
            await ReloadFromDiskAsync(cancellationToken);

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

            logger.LogWarning(ex, "Unable to fetch new package data.");
            return false;
        }
    }

    private async Task DownloadPackageDataAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();

        await using var compressed = await client.GetStreamAsync(options.Value.DataSource.DataFileUrl, cancellationToken);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        await using var output = File.Create(DataFilePath);
        await gzip.CopyToAsync(output, cancellationToken);
    }

    private async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
    {
        var next = await PackageDataLoader.LoadAsync(DataFilePath, cancellationToken);
        store.Replace(next);
    }
}