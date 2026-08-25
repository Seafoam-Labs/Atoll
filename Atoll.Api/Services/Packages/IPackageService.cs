namespace Atoll.Api.Services.Packages;

public interface IPackageService
{
    Task<IReadOnlyList<string>> ListAsync();
    Task<int> CountAsync();
    Task<bool> ExistsAsync(string packageName, CancellationToken ct = default);
    Task<PackageFiles> GetAsync(string packageName, string? commitSha = null);
    Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName);
    Task DeleteAsync(string packageName, CancellationToken ct = default);
    Task SyncFromStorageAsync(string packageName);
    Task SyncToStorageAsync(string packageName);
    Task SeedFromAurAsync(string packageName);
    Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files);

    Task<bool> AppendRevisionFromUpstreamAsync(string packageName, IReadOnlyDictionary<string, string> files,
        CancellationToken ct = default);

    string? GetRepositoryPath(string packageName);
    Task EnsureGitRepositoryAsync(string packageName, CancellationToken ct = default);
}