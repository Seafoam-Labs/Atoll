namespace Atoll.Api.Services.Security;

public interface IPackageSecurityRepository
{
    Task<PackageSecurityScanDocument?> GetAsync(string packageName, string revisionId, CancellationToken ct = default);

    Task<PackageSecurityScanDocument?> GetHeadAsync(string packageName, CancellationToken ct = default);

    Task<IReadOnlyCollection<PackageSecurityScanDocument>> ListForPackageAsync(string packageName, CancellationToken ct = default);

    Task<IReadOnlyList<RevisionScanStatus>> ListStatusesForPackageAsync(string packageName, CancellationToken ct = default);

    Task<IReadOnlyCollection<string>> ListPackageNamesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<HeadScanStatus>> ListHeadStatusesAsync(CancellationToken ct = default);

    Task<HeadScanStatusCounts> CountHeadStatusesAsync(CancellationToken ct = default);

    Task<long> CountPendingAsync(CancellationToken ct = default);

    Task<long> RequeueOutdatedAsync(int currentPolicyVersion, CancellationToken ct = default);

    Task MarkPendingAsync(string packageName, string revisionId, bool isHead, int requiredPolicyVersion, CancellationToken ct = default);

    Task EnsurePendingAsync(string packageName, string revisionId, bool isHead, int requiredPolicyVersion, CancellationToken ct = default);

    Task<PackageSecurityScanDocument?> TryClaimPendingScanAsync(string owner, TimeSpan leaseDuration, int workerPolicyVersion, CancellationToken ct = default);

    Task<bool> CompleteScanAsync(string packageName, string revisionId, string owner, ScanResult result, int policyVersion, CancellationToken ct = default);

    Task<bool> MarkScanErrorAsync(string packageName, string revisionId, string owner, int policyVersion, CancellationToken ct = default);

    Task ReleaseScanClaimAsync(string packageName, string revisionId, string owner, CancellationToken ct = default);

    Task PromoteHeadAsync(string packageName, string newHeadRevisionId, CancellationToken ct = default);

    Task DeleteAsync(string packageName, string revisionId, CancellationToken ct = default);

    Task DeletePackageAsync(string packageName, CancellationToken ct = default);
}
