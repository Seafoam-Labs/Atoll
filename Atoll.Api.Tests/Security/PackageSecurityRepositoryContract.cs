using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Xunit;

namespace Atoll.Api.Tests.Security;

public abstract class PackageSecurityRepositoryContract
{
    private protected abstract IPackageSecurityRepository CreateRepository();

    [Fact]
    public async Task CompleteScanAsync_stamps_policy_version_and_returns_true()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.NotNull(claim);

        var result = new ScanResult(SecurityStatus.Verified, []);
        var persisted = await repo.CompleteScanAsync("pkg", "rev-1", "owner1", result, policyVersion: 2);

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.True(persisted);
        Assert.NotNull(scan);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Verified, scan!.Status);
            Assert.Equal(2, scan.PolicyVersion);
            Assert.Equal(2, scan.RequiredPolicyVersion);
            Assert.NotNull(scan.ScannedAt);
            Assert.Null(scan.LeaseOwner);
            Assert.Null(scan.LeaseUntil);
        });
    }

    [Fact]
    public async Task MarkScanErrorAsync_stamps_policy_version_and_returns_true()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.NotNull(claim);

        var persisted = await repo.MarkScanErrorAsync("pkg", "rev-1", "owner1", policyVersion: 2);

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.True(persisted);
        Assert.NotNull(scan);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Error, scan!.Status);
            Assert.Equal(2, scan.PolicyVersion);
            Assert.Equal(2, scan.RequiredPolicyVersion);
            Assert.NotNull(scan.ScannedAt);
            Assert.Null(scan.LeaseOwner);
            Assert.Null(scan.LeaseUntil);
        });
    }

    [Fact]
    public async Task MarkPendingAsync_resets_policy_version_and_findings()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.NotNull(claim);

        var findings = new List<SecurityFinding>
        {
            new("rule-1", FindingSeverity.High, "msg", "snip", "PKGBUILD")
        };
        await repo.CompleteScanAsync("pkg", "rev-1", "owner1", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 2);

        // Reset via MarkPendingAsync
        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.NotNull(scan);
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, scan!.Status);
            Assert.Null(scan.PolicyVersion);
            Assert.Equal(2, scan.RequiredPolicyVersion);
            Assert.Empty(scan.Findings);
            Assert.Null(scan.ScannedAt);
            Assert.Null(scan.LeaseOwner);
            Assert.Null(scan.LeaseUntil);
        });
    }

    [Fact]
    public async Task MarkPendingAsync_cannot_lower_existing_requirement()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 3);

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.Equal(3, scan!.RequiredPolicyVersion);
    }

    [Fact]
    public async Task EnsurePendingAsync_sets_requirement_on_insert_only()
    {
        var repo = CreateRepository();

        await repo.EnsurePendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        await repo.EnsurePendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 5);

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.NotNull(scan);
        Assert.Equal(SecurityStatus.Pending, scan!.Status);
        Assert.Equal(2, scan.RequiredPolicyVersion);
    }

    [Fact]
    public async Task Older_worker_cannot_claim_work_requiring_newer_policy()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 3);

        var staleClaim = await repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.Null(staleClaim);

        var claim = await repo.TryClaimPendingScanAsync("v3-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        Assert.NotNull(claim);

        var persisted = await repo.CompleteScanAsync(
            "pkg", "rev-1", "v3-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);
        Assert.True(persisted);
    }

    [Fact]
    public async Task Reconciliation_fences_in_flight_claim_from_older_worker()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(10), workerPolicyVersion: 2);
        Assert.NotNull(claim);

        var requeued = await repo.RequeueOutdatedAsync(3);
        Assert.Equal(1, requeued);

        var afterRaise = await repo.GetAsync("pkg", "rev-1");
        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, afterRaise!.Status);
            Assert.Equal(3, afterRaise.RequiredPolicyVersion);
            Assert.Null(afterRaise.LeaseOwner);
            Assert.Null(afterRaise.LeaseUntil);
        });

        var completionRejected = await repo.CompleteScanAsync(
            "pkg", "rev-1", "v2-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);
        var errorRejected = await repo.MarkScanErrorAsync("pkg", "rev-1", "v2-worker", policyVersion: 2);
        Assert.Multiple(() =>
        {
            Assert.False(completionRejected, "late v2 completion is rejected");
            Assert.False(errorRejected, "late v2 error is rejected");
        });

        var stillPending = await repo.GetAsync("pkg", "rev-1");
        Assert.Equal(SecurityStatus.Pending, stillPending!.Status);

        var reclaimer = await repo.TryClaimPendingScanAsync("v3-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        Assert.NotNull(reclaimer);
        var persisted = await repo.CompleteScanAsync(
            "pkg", "rev-1", "v3-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);
        Assert.True(persisted);
    }

    [Fact]
    public async Task Completion_and_error_are_rejected_after_lease_loss()
    {
        var repo = CreateRepository();

        await repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.NotNull(claim);

        await repo.ReleaseScanClaimAsync("pkg", "rev-1", "owner1");
        var completion = await repo.CompleteScanAsync(
            "pkg", "rev-1", "owner1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);
        var error = await repo.MarkScanErrorAsync("pkg", "rev-1", "owner1", policyVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.False(completion);
            Assert.False(error);
        });

        var scan = await repo.GetAsync("pkg", "rev-1");
        Assert.Equal(SecurityStatus.Pending, scan!.Status);
    }

    [Fact]
    public async Task RequeueOutdatedAsync_requeues_older_versions_and_preserves_current_or_newer_versions()
    {
        var repo = CreateRepository();

        // 1. Verified scan from an older policy
        await repo.MarkPendingAsync("legacy-verified", "rev-1", true, requiredPolicyVersion: 1);
        _ = await repo.TryClaimPendingScanAsync("o1", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        await repo.CompleteScanAsync("legacy-verified", "rev-1", "o1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1);

        await repo.MarkPendingAsync("legacy-flagged", "rev-1", true, requiredPolicyVersion: 1);
        _ = await repo.TryClaimPendingScanAsync("o2", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        var findings = new List<SecurityFinding> { new("old-rule", FindingSeverity.Medium, "old finding", "", "PKGBUILD") };
        await repo.CompleteScanAsync("legacy-flagged", "rev-1", "o2", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 1);

        // 3. Error from an older policy
        await repo.MarkPendingAsync("v1-error", "rev-1", false, requiredPolicyVersion: 1);
        _ = await repo.TryClaimPendingScanAsync("o3", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        await repo.MarkScanErrorAsync("v1-error", "rev-1", "o3", policyVersion: 1);

        // 4. Document scanned under the current policy
        await repo.MarkPendingAsync("v2-verified", "rev-1", true, requiredPolicyVersion: 2);
        _ = await repo.TryClaimPendingScanAsync("o4", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        await repo.CompleteScanAsync("v2-verified", "rev-1", "o4", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);

        // 5. Document produced by a newer worker during a rolling deployment
        await repo.MarkPendingAsync("v3-verified", "rev-1", true, requiredPolicyVersion: 3);
        _ = await repo.TryClaimPendingScanAsync("o5", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        await repo.CompleteScanAsync("v3-verified", "rev-1", "o5", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);

        // 6. Pending scan with an outdated requirement
        await repo.MarkPendingAsync("already-pending", "rev-1", true, requiredPolicyVersion: 1);

        // Requeue outdated with current version 2
        var requeued = await repo.RequeueOutdatedAsync(2);
        Assert.Equal(4, requeued);

        // Verify status resets
        var doc1 = await repo.GetAsync("legacy-verified", "rev-1");
        Assert.Equal(SecurityStatus.Pending, doc1!.Status);
        Assert.Null(doc1.PolicyVersion);
        Assert.Equal(2, doc1.RequiredPolicyVersion);
        Assert.Null(doc1.ScannedAt);

        var doc2 = await repo.GetAsync("legacy-flagged", "rev-1");
        Assert.Equal(SecurityStatus.Pending, doc2!.Status);
        Assert.Null(doc2.PolicyVersion);
        Assert.Empty(doc2.Findings);

        var doc3 = await repo.GetAsync("v1-error", "rev-1");
        Assert.Equal(SecurityStatus.Pending, doc3!.Status);
        Assert.Null(doc3.PolicyVersion);
        Assert.False(doc3.IsHead);

        var doc6 = await repo.GetAsync("already-pending", "rev-1");
        Assert.Equal(SecurityStatus.Pending, doc6!.Status);
        Assert.Equal(2, doc6.RequiredPolicyVersion);

        var doc4 = await repo.GetAsync("v2-verified", "rev-1");
        Assert.Equal(SecurityStatus.Verified, doc4!.Status);
        Assert.Equal(2, doc4.PolicyVersion);

        var doc5 = await repo.GetAsync("v3-verified", "rev-1");
        Assert.Equal(SecurityStatus.Verified, doc5!.Status);
        Assert.Equal(3, doc5.PolicyVersion);

        // Idempotency
        var requeuedAgain = await repo.RequeueOutdatedAsync(2);
        Assert.Equal(0, requeuedAgain);
    }

    [Fact]
    public async Task RequeueOutdatedAsync_cannot_lower_newer_requirement()
    {
        var repo = CreateRepository();

        // Completed result produced by a v4 worker.
        await repo.MarkPendingAsync("done-v4", "rev-1", true, requiredPolicyVersion: 4);
        _ = await repo.TryClaimPendingScanAsync("o1", TimeSpan.FromMinutes(1), workerPolicyVersion: 4);
        await repo.CompleteScanAsync("done-v4", "rev-1", "o1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 4);
        // Pending work already raised to v4 by a newer reconciler.
        await repo.MarkPendingAsync("pending-v4", "rev-1", true, requiredPolicyVersion: 4);

        var requeued = await repo.RequeueOutdatedAsync(3);
        Assert.Equal(0, requeued);

        var pending = await repo.GetAsync("pending-v4", "rev-1");
        var done = await repo.GetAsync("done-v4", "rev-1");
        Assert.Multiple(() =>
        {
            Assert.Equal(4, pending!.RequiredPolicyVersion);
            Assert.Equal(SecurityStatus.Pending, pending.Status);
            Assert.Equal(4, done!.RequiredPolicyVersion);
            Assert.Equal(SecurityStatus.Verified, done.Status);
            Assert.Equal(4, done.PolicyVersion);
        });
    }
}
