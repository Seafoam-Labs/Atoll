namespace Atoll.Api.Services.Aur;

public record PackageFiles(Dictionary<string, string> Files);

public record PackageVersion(
    string Sha,
    DateTimeOffset Date,
    string Message,
    string Author);

public interface IPackageRepository
{
    Task<IReadOnlyList<string>> ListAsync();
    Task<bool> ExistsAsync(string packageName);
    Task CreateAsync(string packageName, PackageFiles files, string message);
    Task UpdateAsync(string packageName, PackageFiles files, string message);
    Task<PackageFiles> GetAsync(string packageName, string? commitSha = null);
    Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName);
    Task DeleteAsync(string packageName);
    Task SyncFromStorageAsync(string packageName);
    Task SyncToStorageAsync(string packageName);
    Task SeedFromAurAsync(string packageName);
}