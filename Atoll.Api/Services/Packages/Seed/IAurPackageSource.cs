namespace Atoll.Api.Services.Packages.Seed;

public interface IAurPackageSource
{
    Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(string packageBase, CancellationToken ct = default);
}
