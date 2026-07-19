namespace Atoll.Api.Services.Packages;

public interface IPackageRepository
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);

    Task<bool> ExistsAsync(string packageName, CancellationToken ct = default);

    Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default);

    Task<PackageRevisionDocument?> GetRevisionAsync(
        string packageName,
        string revisionId,
        CancellationToken ct = default);

    Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName, CancellationToken ct = default);

    Task InsertSeedAsync(PackageDocument doc, CancellationToken ct = default);

    Task AppendRevisionAsync(
        string packageName,
        PackageRevisionDocument revision,
        Dictionary<string, PackageFile> headFiles,
        int maxRevisions,
        CancellationToken ct = default);

    Task DeleteAsync(string packageName, CancellationToken ct = default);
}