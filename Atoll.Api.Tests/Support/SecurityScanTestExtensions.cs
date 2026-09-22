using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Xunit;

namespace Atoll.Api.Tests.Support;

internal static class SecurityScanTestExtensions
{
    /// <summary>
    ///     Queues a revision as the head scan and runs the claim-then-complete cycle the worker uses,
    ///     so the revision ends in <paramref name="status"/>. A Pending verdict leaves it queued.
    ///     Pass an explicit policy version to the repository directly when a test needs another one.
    /// </summary>
    internal static async Task ScanRevisionAsync(
        this IPackageSecurityRepository security,
        string packageName,
        string revisionId,
        SecurityStatus status,
        params SecurityFinding[] findings)
    {
        await security.MarkPendingAsync(packageName, revisionId, true,
            PkgBuildSecurityScanner.CurrentPolicyVersion, TestContext.Current.CancellationToken);
        if (status == SecurityStatus.Pending) return;

        await security.CompleteScanAsync(packageName, status, findings);
    }

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
        var ct = TestContext.Current.CancellationToken;
        var claim = await security.TryClaimPendingScanAsync("test-owner", TimeSpan.FromMinutes(1),
                PkgBuildSecurityScanner.CurrentPolicyVersion, ct)
            ?? throw new InvalidOperationException("expected a pending scan to be claimable");
        await security.CompleteScanAsync(packageName, claim.RevisionId, "test-owner",
            new ScanResult(status, [.. findings]), PkgBuildSecurityScanner.CurrentPolicyVersion, ct);
        return claim.RevisionId;
    }

    internal static Task MarkHeadVerifiedAsync(this IPackageSecurityRepository security, string packageName)
    {
        return security.CompleteScanAsync(packageName, SecurityStatus.Verified);
    }
}
