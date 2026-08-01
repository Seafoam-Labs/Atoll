namespace Atoll.Api.Services.Security;

public interface IPackageSecurityRepository
{
    Task<PackageSecurityScanDocument?> GetAsync(string packageName, CancellationToken ct = default);

    Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default);

    Task MarkPendingAsync(string packageName, string revisionId, CancellationToken ct = default);

    Task EnsurePendingAsync(string packageName, string revisionId, CancellationToken ct = default);

    Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default);

    Task CompleteScanAsync(
        string packageName,
        string revisionId,
        string owner,
        ScanResult result,
        CancellationToken ct = default);

    Task MarkScanErrorAsync(
        string packageName,
        string revisionId,
        string owner,
        CancellationToken ct = default);

    Task ReleaseScanClaimAsync(string packageName, string owner, CancellationToken ct = default);
}