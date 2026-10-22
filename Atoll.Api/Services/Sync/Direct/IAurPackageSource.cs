namespace Atoll.Api.Services.Sync.Direct;

public interface IAurPackageSource
{
    Task<IReadOnlyDictionary<string, byte[]>> FetchFilesAsync(string packageBase, CancellationToken ct = default);
}
