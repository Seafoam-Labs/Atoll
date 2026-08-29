using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using MongoDB.Driver;
using NUnit.Framework;

namespace Atoll.Api.Tests.Security;

[Category("RequiresMongo")]
public class MongoPackageSecurityRepositoryTests
{
    private IMongoClient _client = null!;
    private string _database = null!;
    private MongoPackageSecurityRepository _repo = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(MongoFixture.IsAvailable, Is.True, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
        _repo = MongoRepositoryFactory.CreatePackageSecurityRepository(_client, _database);
    }

    [TearDown]
    public async Task TearDown()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
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

        // Call MarkPendingAsync on existing scanned doc
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
    public async Task Legacy_pending_work_without_requirement_is_claimable_and_backfilled()
    {
        var collection = _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-pending", "rev-1"),
            PackageName = "legacy-pending",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Pending,
            Findings = []
        });

        var claim = await _repo.TryClaimPendingScanAsync("v2-worker", TimeSpan.FromMinutes(1), workerPolicyVersion: 2);
        Assert.That(claim, Is.Not.Null, "a missing requirement is treated as unconstrained");

        await _repo.ReleaseScanClaimAsync("legacy-pending", "rev-1", "v2-worker");

        var requeued = await _repo.RequeueOutdatedAsync(3);
        Assert.That(requeued, Is.EqualTo(1));

        var scan = await _repo.GetAsync("legacy-pending", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.RequiredPolicyVersion, Is.EqualTo(3), "reconciliation backfills the requirement");
            Assert.That(scan.Status, Is.EqualTo(SecurityStatus.Pending));
        });
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
    public async Task RequeueOutdatedAsync_requeues_unversioned_and_older_versions_only()
    {
        // 1. Legacy unversioned verified document (directly inserted into Mongo)
        var collection = _client.GetDatabase(_database).GetCollection<PackageSecurityScanDocument>("package-security-scans");
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-verified", "rev-1"),
            PackageName = "legacy-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = null,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-5),
            Findings = []
        });

        // 2. Legacy unversioned flagged document with findings
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("legacy-flagged", "rev-1"),
            PackageName = "legacy-flagged",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Flagged,
            PolicyVersion = null,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-5),
            Findings = [new SecurityFinding("rule-old", FindingSeverity.High, "old issue", "", "PKGBUILD")]
        });

        // 3. Document scanned under older policy version (e.g. 1)
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v1-error", "rev-1"),
            PackageName = "v1-error",
            RevisionId = "rev-1",
            IsHead = false,
            Status = SecurityStatus.Error,
            PolicyVersion = 1,
            ScannedAt = DateTimeOffset.UtcNow.AddDays(-2),
            Findings = []
        });

        // 4. Document scanned under current policy version (e.g. 2)
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v2-verified", "rev-1"),
            PackageName = "v2-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = 2,
            ScannedAt = DateTimeOffset.UtcNow,
            Findings = []
        });

        // 5. Document produced by a newer worker during a rolling deployment
        await collection.InsertOneAsync(new PackageSecurityScanDocument
        {
            Id = PackageSecurityScanDocument.ComposeId("v3-verified", "rev-1"),
            PackageName = "v3-verified",
            RevisionId = "rev-1",
            IsHead = true,
            Status = SecurityStatus.Verified,
            PolicyVersion = 3,
            ScannedAt = DateTimeOffset.UtcNow,
            Findings = []
        });

        // 6. Document currently Pending with an outdated requirement
        await _repo.MarkPendingAsync("already-pending", "rev-1", true, requiredPolicyVersion: 1);

        // Requeue outdated scans with current policy version 2
        var requeuedCount = await _repo.RequeueOutdatedAsync(2);
        Assert.That(requeuedCount, Is.EqualTo(4), "three completed outcomes plus one pending requirement are updated");

        // Verify legacy-verified is now Pending, unversioned, timestamps cleared
        var doc1 = await _repo.GetAsync("legacy-verified", "rev-1");
        Assert.That(doc1, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc1!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc1.PolicyVersion, Is.Null);
            Assert.That(doc1.RequiredPolicyVersion, Is.EqualTo(2), "requeued work now requires the current policy");
            Assert.That(doc1.ScannedAt, Is.Null);
            Assert.That(doc1.Findings, Is.Empty);
            Assert.That(doc1.IsHead, Is.True);
        });

        // Verify legacy-flagged is now Pending, findings cleared
        var doc2 = await _repo.GetAsync("legacy-flagged", "rev-1");
        Assert.That(doc2, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc2!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc2.PolicyVersion, Is.Null);
            Assert.That(doc2.Findings, Is.Empty);
        });

        // Verify v1-error is now Pending, IsHead preserved
        var doc3 = await _repo.GetAsync("v1-error", "rev-1");
        Assert.That(doc3, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc3!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(doc3.PolicyVersion, Is.Null);
            Assert.That(doc3.IsHead, Is.False);
        });

        // Verify the pending requirement was raised in place
        var doc6 = await _repo.GetAsync("already-pending", "rev-1");
        Assert.That(doc6, Is.Not.Null);
        Assert.That(doc6!.RequiredPolicyVersion, Is.EqualTo(2));

        // Verify v2-verified is unchanged
        var doc4 = await _repo.GetAsync("v2-verified", "rev-1");
        Assert.That(doc4, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc4!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(doc4.PolicyVersion, Is.EqualTo(2));
        });

        // Verify a newer result is not downgraded by an older worker
        var doc5 = await _repo.GetAsync("v3-verified", "rev-1");
        Assert.That(doc5, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(doc5!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(doc5.PolicyVersion, Is.EqualTo(3));
        });

        // Idempotency: Running RequeueOutdatedAsync again returns 0
        var secondRequeue = await _repo.RequeueOutdatedAsync(2);
        Assert.That(secondRequeue, Is.EqualTo(0));
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
