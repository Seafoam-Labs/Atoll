namespace Atoll.Api.Services.Packages.Persistence;

public interface IPackageRepository
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);

    Task<long> CountAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PackageIndexEntry>> ListIndexPageAsync(int skip, int take, CancellationToken ct = default);

    Task<bool> ExistsAsync(string packageName, CancellationToken ct = default);

    Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default);

    Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default);

    Task<PackageRevisionContentDocument?> GetRevisionAsync(string packageName, string revisionId, CancellationToken ct = default);

    Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName, CancellationToken ct = default);

    Task InsertSeedAsync(PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default);

    Task AppendRevisionAsync(string packageName, PackageRevisionContentDocument revision, int maxRevisions,
        CancellationToken ct = default);

    Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default);

    Task UpdateSyncStateAsync(IReadOnlyCollection<string> packageNames, string? upstreamHead, bool succeeded, string? error,
        CancellationToken ct = default);

    Task DeleteAsync(string packageName, CancellationToken ct = default);
}