using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

public class InMemoryPackageSecurityRepositoryTests
{
    private InMemoryPackageSecurityRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = new InMemoryPackageSecurityRepository();
    }

    [Test]
    public async Task CompleteScanAsync_stamps_policy_version_and_returns_true()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null);

        var result = new ScanResult(SecurityStatus.Verified, []);
        var persisted = await _repo.CompleteScanAsync("pkg", "rev-1", "owner1", result, policyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(persisted, Is.True);
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(scan.PolicyVersion, Is.EqualTo(2));
            Assert.That(scan.RequiredPolicyVersion, Is.EqualTo(2), "requirement is retained after completion");
            Assert.That(scan.ScannedAt, Is.Not.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task MarkScanErrorAsync_stamps_policy_version_and_returns_true()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null);

        var persisted = await _repo.MarkScanErrorAsync("pkg", "rev-1", "owner1", policyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(persisted, Is.True);
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Error));
            Assert.That(scan.PolicyVersion, Is.EqualTo(2));
            Assert.That(scan.RequiredPolicyVersion, Is.EqualTo(2), "requirement is retained after error");
            Assert.That(scan.ScannedAt, Is.Not.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task MarkPendingAsync_resets_policy_version_and_findings()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null);

        var findings = new List<SecurityFinding>
        {
            new("rule-1", FindingSeverity.High, "msg", "snip", "PKGBUILD")
        };
        await _repo.CompleteScanAsync("pkg", "rev-1", "owner1", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 2);

        // Reset via MarkPendingAsync
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(scan.PolicyVersion, Is.Null);
            Assert.That(scan.RequiredPolicyVersion, Is.EqualTo(2), "requirement survives the reset");
            Assert.That(scan.Findings, Is.Empty);
            Assert.That(scan.ScannedAt, Is.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task MarkPendingAsync_cannot_lower_existing_requirement()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 3);

        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan!.RequiredPolicyVersion, Is.EqualTo(3));
    }

    [Test]
    public async Task EnsurePendingAsync_sets_requirement_on_insert_only()
    {
        await _repo.EnsurePendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        await _repo.EnsurePendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 5);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(scan.RequiredPolicyVersion, Is.EqualTo(2), "existing documents are left untouched");
    }

    [Test]
    public async Task Older_worker_cannot_claim_work_requiring_newer_policy()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 3);

        var staleClaim = await _repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(staleClaim, Is.Null, "a v2 worker must not claim work requiring v3");

        var claim = await _repo.TryClaimPendingScanAsync("v3-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        Assert.That(claim, Is.Not.Null);

        var persisted = await _repo.CompleteScanAsync(
            "pkg", "rev-1", "v3-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);
        Assert.That(persisted, Is.True);
    }

    [Test]
    public async Task Reconciliation_fences_in_flight_claim_from_older_worker()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await _repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(10), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null);

        var requeued = await _repo.RequeueOutdatedAsync(3);
        Assert.That(requeued, Is.EqualTo(1));

        var afterRaise = await _repo.GetAsync("pkg", "rev-1");
        Assert.Multiple(() =>
        {
            Assert.That(afterRaise!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(afterRaise.RequiredPolicyVersion, Is.EqualTo(3));
            Assert.That(afterRaise.LeaseOwner, Is.Null, "the v2 lease is cleared so the requirement is enforced immediately");
            Assert.That(afterRaise.LeaseUntil, Is.Null);
        });

        var completionRejected = await _repo.CompleteScanAsync(
            "pkg", "rev-1", "v2-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);
        var errorRejected = await _repo.MarkScanErrorAsync("pkg", "rev-1", "v2-worker", policyVersion: 2);
        Assert.Multiple(() =>
        {
            Assert.That(completionRejected, Is.False, "late v2 completion is rejected");
            Assert.That(errorRejected, Is.False, "late v2 error is rejected");
        });

        var stillPending = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(stillPending!.Status, Is.EqualTo(SecurityStatus.Pending));

        var reclaimer = await _repo.TryClaimPendingScanAsync("v3-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        Assert.That(reclaimer, Is.Not.Null);
        var persisted = await _repo.CompleteScanAsync(
            "pkg", "rev-1", "v3-worker", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);
        Assert.That(persisted, Is.True);
    }

    [Test]
    public async Task Completion_and_error_are_rejected_after_lease_loss()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true, requiredPolicyVersion: 2);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null);

        await _repo.ReleaseScanClaimAsync("pkg", "rev-1", "owner1");
        var completion = await _repo.CompleteScanAsync(
            "pkg", "rev-1", "owner1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);
        var error = await _repo.MarkScanErrorAsync("pkg", "rev-1", "owner1", policyVersion: 2);

        Assert.Multiple(() =>
        {
            Assert.That(completion, Is.False);
            Assert.That(error, Is.False);
        });

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
    }

    [Test]
    public async Task RequeueOutdatedAsync_requeues_older_versions_and_preserves_current_or_newer_versions()
    {
        // 1. Verified scan from an older policy
        await _repo.MarkPendingAsync("legacy-verified", "rev-1", true, requiredPolicyVersion: 1);
        _ = await _repo.TryClaimPendingScanAsync("o1", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        await _repo.CompleteScanAsync("legacy-verified", "rev-1", "o1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1);

        await _repo.MarkPendingAsync("legacy-flagged", "rev-1", true, requiredPolicyVersion: 1);
        _ = await _repo.TryClaimPendingScanAsync("o2", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        var findings = new List<SecurityFinding> { new("old-rule", FindingSeverity.Medium, "old finding", "", "PKGBUILD") };
        await _repo.CompleteScanAsync("legacy-flagged", "rev-1", "o2", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 1);

        // 3. Error from an older policy
        await _repo.MarkPendingAsync("v1-error", "rev-1", false, requiredPolicyVersion: 1);
        _ = await _repo.TryClaimPendingScanAsync("o3", TimeSpan.FromMinutes(1), workerPolicyVersion: 1);
        await _repo.MarkScanErrorAsync("v1-error", "rev-1", "o3", policyVersion: 1);

        // 4. Document scanned under the current policy
        await _repo.MarkPendingAsync("v2-verified", "rev-1", true, requiredPolicyVersion: 2);
        _ = await _repo.TryClaimPendingScanAsync("o4", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        await _repo.CompleteScanAsync("v2-verified", "rev-1", "o4", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);

        // 5. Document produced by a newer worker during a rolling deployment
        await _repo.MarkPendingAsync("v3-verified", "rev-1", true, requiredPolicyVersion: 3);
        _ = await _repo.TryClaimPendingScanAsync("o5", TimeSpan.FromMinutes(1), workerPolicyVersion: 3);
        await _repo.CompleteScanAsync("v3-verified", "rev-1", "o5", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);

        // 6. Pending scan with an outdated requirement
        await _repo.MarkPendingAsync("already-pending", "rev-1", true, requiredPolicyVersion: 1);

        // Requeue outdated with current version 2
        var requeued = await _repo.RequeueOutdatedAsync(2);
        Assert.That(requeued, Is.EqualTo(4), "three completed outcomes plus one pending requirement are updated");

        // Verify status resets
        var doc1 = await _repo.GetAsync("legacy-verified", "rev-1");
        Assert.That(doc1!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc1.PolicyVersion, Is.Null);
        Assert.That(doc1.RequiredPolicyVersion, Is.EqualTo(2), "requeued work now requires the current policy");
        Assert.That(doc1.ScannedAt, Is.Null);

        var doc2 = await _repo.GetAsync("legacy-flagged", "rev-1");
        Assert.That(doc2!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc2.PolicyVersion, Is.Null);
        Assert.That(doc2.Findings, Is.Empty);

        var doc3 = await _repo.GetAsync("v1-error", "rev-1");
        Assert.That(doc3!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc3.PolicyVersion, Is.Null);
        Assert.That(doc3.IsHead, Is.False);

        var doc6 = await _repo.GetAsync("already-pending", "rev-1");
        Assert.That(doc6!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc6.RequiredPolicyVersion, Is.EqualTo(2), "the pending requirement is raised in place");

        var doc4 = await _repo.GetAsync("v2-verified", "rev-1");
        Assert.That(doc4!.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(doc4.PolicyVersion, Is.EqualTo(2));

        var doc5 = await _repo.GetAsync("v3-verified", "rev-1");
        Assert.That(doc5!.Status, Is.EqualTo(SecurityStatus.Verified));
        Assert.That(doc5.PolicyVersion, Is.EqualTo(3));

        // Idempotency
        var requeuedAgain = await _repo.RequeueOutdatedAsync(2);
        Assert.That(requeuedAgain, Is.EqualTo(0));
    }

    [Test]
    public async Task RequeueOutdatedAsync_cannot_lower_newer_requirement()
    {
        // Completed result produced by a v4 worker.
        await _repo.MarkPendingAsync("done-v4", "rev-1", true, requiredPolicyVersion: 4);
        _ = await _repo.TryClaimPendingScanAsync("o1", TimeSpan.FromMinutes(1), workerPolicyVersion: 4);
        await _repo.CompleteScanAsync("done-v4", "rev-1", "o1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 4);
        // Pending work already raised to v4 by a newer reconciler.
        await _repo.MarkPendingAsync("pending-v4", "rev-1", true, requiredPolicyVersion: 4);

        var requeued = await _repo.RequeueOutdatedAsync(3);
        Assert.That(requeued, Is.EqualTo(0));

        var pending = await _repo.GetAsync("pending-v4", "rev-1");
        var done = await _repo.GetAsync("done-v4", "rev-1");
        Assert.Multiple(() =>
        {
            Assert.That(pending!.RequiredPolicyVersion, Is.EqualTo(4));
            Assert.That(pending.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(done!.RequiredPolicyVersion, Is.EqualTo(4));
            Assert.That(done.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(done.PolicyVersion, Is.EqualTo(4));
        });
    }
}
