namespace Atoll.Api.Services.Catalog.Persistence;

public interface IAurMetadataRepository
{
    Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct);

    Task SyncAsync(AurMetadataDelta delta, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);

    Task DeleteAsync(CancellationToken ct);
}
