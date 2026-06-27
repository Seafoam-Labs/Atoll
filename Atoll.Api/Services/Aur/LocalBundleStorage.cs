using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Aur;

public sealed class LocalBundleStorage : IBundleStorage
{
    private readonly string _bundlePath;

    public LocalBundleStorage(IOptions<AtollOptions> options)
    {
        _bundlePath = Path.Combine(options.Value.Storage.Local.DataPath, "bundles");
        Directory.CreateDirectory(_bundlePath);
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_bundlePath))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var packages = Directory
            .EnumerateFiles(_bundlePath, "*.bundle")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(packages);
    }

    public Task<bool> ExistsAsync(string packageName, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(BundlePath(packageName)));
    }

    public Task DownloadAsync(string packageName, string destinationPath, CancellationToken cancellationToken = default)
    {
        File.Copy(BundlePath(packageName), destinationPath, true);
        return Task.CompletedTask;
    }

    public Task UploadAsync(string packageName, string sourcePath, CancellationToken cancellationToken = default)
    {
        File.Copy(sourcePath, BundlePath(packageName), true);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string packageName, CancellationToken cancellationToken = default)
    {
        File.Delete(BundlePath(packageName));
        return Task.CompletedTask;
    }

    private string BundlePath(string packageName)
    {
        return Path.Combine(_bundlePath, $"{packageName}.bundle");
    }
}