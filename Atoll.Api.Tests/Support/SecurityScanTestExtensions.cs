using Atoll.Api.Services.Security;

namespace Atoll.Api.Tests.Support;

internal static class SecurityScanTestExtensions
{
    /// <summary>
    ///     Claims the oldest unclaimed pending scan and completes it with the given verdict,
    ///     simulating the scan worker. Returns the scanned revision id.
    /// </summary>
    internal static async Task<string> CompleteScanAsync(
        this IPackageSecurityRepository security,
        string packageName,
        SecurityStatus status,
        params SecurityFinding[] findings)
    {
        var claim = await security.TryClaimPendingScanAsync("test-owner", TimeSpan.FromMinutes(1))
            ?? throw new InvalidOperationException("expected a pending scan to be claimable");
        await security.CompleteScanAsync(packageName, claim.RevisionId, "test-owner",
            new ScanResult(status, [.. findings]), PkgBuildSecurityScanner.CurrentPolicyVersion);
        return claim.RevisionId;
    }

    internal static Task MarkHeadVerifiedAsync(this IPackageSecurityRepository security, string packageName)
    {
        return security.CompleteScanAsync(packageName, SecurityStatus.Verified);
    }
}
