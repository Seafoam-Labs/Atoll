namespace Atoll.Api.Services.Sync.Direct;

public interface IAurPackageSource
{
    Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(string packageBase, CancellationToken ct = default);
}
