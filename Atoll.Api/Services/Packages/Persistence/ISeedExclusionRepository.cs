namespace Atoll.Api.Services.Packages.Persistence;

public interface ISeedExclusionRepository
{
    Task<IReadOnlySet<string>> ListDocumentTooLargePackageBasesAsync(CancellationToken ct = default);

    Task RecordDocumentTooLargeAsync(
        string packageBase,
        IReadOnlyList<string> packageNames,
        long serializedSizeBytes,
        CancellationToken ct = default);
}
