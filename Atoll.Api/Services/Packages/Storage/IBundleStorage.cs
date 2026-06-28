namespace Atoll.Api.Services.Packages.Storage;

public interface IBundleStorage
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string packageName, CancellationToken cancellationToken = default);
    Task DownloadAsync(string packageName, string destinationPath, CancellationToken cancellationToken = default);
    Task UploadAsync(string packageName, string sourcePath, CancellationToken cancellationToken = default);
    Task DeleteAsync(string packageName, CancellationToken cancellationToken = default);
}