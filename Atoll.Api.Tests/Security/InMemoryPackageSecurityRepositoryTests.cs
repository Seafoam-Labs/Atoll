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
    public async Task CompleteScanAsync_stamps_policy_version()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1));
        Assert.That(claim, Is.Not.Null);

        var result = new ScanResult(SecurityStatus.Verified, []);
        await _repo.CompleteScanAsync("pkg", "rev-1", "owner1", result, policyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Verified));
            Assert.That(scan.PolicyVersion, Is.EqualTo(2));
            Assert.That(scan.ScannedAt, Is.Not.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task MarkScanErrorAsync_stamps_policy_version()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1));
        Assert.That(claim, Is.Not.Null);

        await _repo.MarkScanErrorAsync("pkg", "rev-1", "owner1", policyVersion: 2);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Error));
            Assert.That(scan.PolicyVersion, Is.EqualTo(2));
            Assert.That(scan.ScannedAt, Is.Not.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task MarkPendingAsync_resets_policy_version_and_findings()
    {
        await _repo.MarkPendingAsync("pkg", "rev-1", true);
        var claim = await _repo.TryClaimPendingScanAsync("owner1", TimeSpan.FromMinutes(1));
        Assert.That(claim, Is.Not.Null);

        var findings = new List<SecurityFinding>
        {
            new("rule-1", FindingSeverity.High, "msg", "snip", "PKGBUILD")
        };
        await _repo.CompleteScanAsync("pkg", "rev-1", "owner1", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 2);

        // Reset via MarkPendingAsync
        await _repo.MarkPendingAsync("pkg", "rev-1", true);

        var scan = await _repo.GetAsync("pkg", "rev-1");
        Assert.That(scan, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(scan!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(scan.PolicyVersion, Is.Null);
            Assert.That(scan.Findings, Is.Empty);
            Assert.That(scan.ScannedAt, Is.Null);
            Assert.That(scan.LeaseOwner, Is.Null);
            Assert.That(scan.LeaseUntil, Is.Null);
        });
    }

    [Test]
    public async Task RequeueOutdatedAsync_requeues_older_versions_and_preserves_current_or_newer_versions()
    {
        // 1. Verified scan from an older policy
        await _repo.MarkPendingAsync("legacy-verified", "rev-1", true);
        _ = await _repo.TryClaimPendingScanAsync("o1", TimeSpan.FromMinutes(1));
        await _repo.CompleteScanAsync("legacy-verified", "rev-1", "o1", new ScanResult(SecurityStatus.Verified, []), policyVersion: 1);

        await _repo.MarkPendingAsync("legacy-flagged", "rev-1", true);
        _ = await _repo.TryClaimPendingScanAsync("o2", TimeSpan.FromMinutes(1));
        var findings = new List<SecurityFinding> { new("old-rule", FindingSeverity.Medium, "old finding", "", "PKGBUILD") };
        await _repo.CompleteScanAsync("legacy-flagged", "rev-1", "o2", new ScanResult(SecurityStatus.Flagged, findings), policyVersion: 1);

        // 3. Error from an older policy
        await _repo.MarkPendingAsync("v1-error", "rev-1", false);
        _ = await _repo.TryClaimPendingScanAsync("o3", TimeSpan.FromMinutes(1));
        await _repo.MarkScanErrorAsync("v1-error", "rev-1", "o3", policyVersion: 1);

        // 4. Document scanned under the current policy
        await _repo.MarkPendingAsync("v2-verified", "rev-1", true);
        _ = await _repo.TryClaimPendingScanAsync("o4", TimeSpan.FromMinutes(1));
        await _repo.CompleteScanAsync("v2-verified", "rev-1", "o4", new ScanResult(SecurityStatus.Verified, []), policyVersion: 2);

        // 5. Document produced by a newer worker during a rolling deployment
        await _repo.MarkPendingAsync("v3-verified", "rev-1", true);
        _ = await _repo.TryClaimPendingScanAsync("o5", TimeSpan.FromMinutes(1));
        await _repo.CompleteScanAsync("v3-verified", "rev-1", "o5", new ScanResult(SecurityStatus.Verified, []), policyVersion: 3);

        // 6. Pending scan
        await _repo.MarkPendingAsync("already-pending", "rev-1", true);

        // Requeue outdated with current version 2
        var requeued = await _repo.RequeueOutdatedAsync(2);
        Assert.That(requeued, Is.EqualTo(3));

        // Verify status resets
        var doc1 = await _repo.GetAsync("legacy-verified", "rev-1");
        Assert.That(doc1!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc1.PolicyVersion, Is.Null);
        Assert.That(doc1.ScannedAt, Is.Null);

        var doc2 = await _repo.GetAsync("legacy-flagged", "rev-1");
        Assert.That(doc2!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc2.PolicyVersion, Is.Null);
        Assert.That(doc2.Findings, Is.Empty);

        var doc3 = await _repo.GetAsync("v1-error", "rev-1");
        Assert.That(doc3!.Status, Is.EqualTo(SecurityStatus.Pending));
        Assert.That(doc3.PolicyVersion, Is.Null);
        Assert.That(doc3.IsHead, Is.False);

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
}
