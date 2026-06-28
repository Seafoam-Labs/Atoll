namespace Atoll.Api.Services.Packages;

public interface IPackageService
{
    Task<IReadOnlyList<string>> ListAsync();
    Task<bool> ExistsAsync(string packageName);
    Task<PackageFiles> GetAsync(string packageName, string? commitSha = null);
    Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName);
    Task DeleteAsync(string packageName);
    Task SyncFromStorageAsync(string packageName);
    Task SyncToStorageAsync(string packageName);
    Task SeedFromAurAsync(string packageName);
}