namespace Atoll.Api.Services.Search.Indexing;

public interface IAurMetadataRepository
{
    Task SaveAsync(IEnumerable<AurPackageMetadata> packages, CancellationToken ct);

    Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct);

    Task<bool> ExistsAsync(CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}