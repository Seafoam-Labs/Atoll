using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Security;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Git;

public class GitRepositoryCacheTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public void IsRevisionServable_security_disabled_serves_every_revision()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitRepositoryCache.IsRevisionServable(false, null), Is.True);
            Assert.That(GitRepositoryCache.IsRevisionServable(false, SecurityStatus.Pending), Is.True);
            Assert.That(GitRepositoryCache.IsRevisionServable(false, SecurityStatus.Verified), Is.True);
            Assert.That(GitRepositoryCache.IsRevisionServable(false, SecurityStatus.Flagged), Is.True);
            Assert.That(GitRepositoryCache.IsRevisionServable(false, SecurityStatus.Error), Is.True);
        });
    }

    [Test]
    public void IsRevisionServable_security_enabled_serves_only_verified_revisions()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitRepositoryCache.IsRevisionServable(true, SecurityStatus.Verified), Is.True);
            Assert.That(GitRepositoryCache.IsRevisionServable(true, SecurityStatus.Pending), Is.False);
            Assert.That(GitRepositoryCache.IsRevisionServable(true, SecurityStatus.Flagged), Is.False);
            Assert.That(GitRepositoryCache.IsRevisionServable(true, SecurityStatus.Error), Is.False);
            Assert.That(GitRepositoryCache.IsRevisionServable(true, null), Is.False);
        });
    }

    [Test]
    public void ComputeHistoryMarker_security_disabled_ignores_scan_statuses()
    {
        var doc = TestDoc("rev-3", ("rev-1", T0), ("rev-2", T1), ("rev-3", T2));
        var statuses = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Flagged,
            ["rev-2"] = SecurityStatus.Verified,
            ["rev-3"] = SecurityStatus.Verified
        };

        var without = GitRepositoryCache.ComputeHistoryMarker(doc, false, null);
        var with = GitRepositoryCache.ComputeHistoryMarker(doc, false, statuses);

        Assert.Multiple(() =>
        {
            Assert.That(without, Is.EqualTo("rev-3\nrev-1\nrev-2\nrev-3"));
            Assert.That(with, Is.EqualTo(without), "statuses must not affect the marker when security is disabled");
        });
    }

    [Test]
    public void ComputeHistoryMarker_security_enabled_includes_statuses_in_materialization_order()
    {
        // Revisions are stored newest-first; the marker must enumerate them in CreatedAt order.
        var doc = TestDoc("rev-3", ("rev-3", T2), ("rev-1", T0), ("rev-2", T1));
        var statuses = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Pending,
            ["rev-2"] = SecurityStatus.Verified,
            ["rev-3"] = SecurityStatus.Flagged
        };

        Assert.That(GitRepositoryCache.ComputeHistoryMarker(doc, true, statuses),
            Is.EqualTo("rev-3\nrev-1:Pending\nrev-2:Verified\nrev-3:Flagged"));
    }

    [Test]
    public void ComputeHistoryMarker_changes_when_a_scan_status_flips()
    {
        var doc = TestDoc("rev-2", ("rev-1", T0), ("rev-2", T1));
        var flagged = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Flagged,
            ["rev-2"] = SecurityStatus.Verified
        };
        var verified = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Verified,
            ["rev-2"] = SecurityStatus.Verified
        };

        Assert.That(GitRepositoryCache.ComputeHistoryMarker(doc, true, flagged),
            Is.Not.EqualTo(GitRepositoryCache.ComputeHistoryMarker(doc, true, verified)),
            "a rescan flipping a revision's status must invalidate the marker");
    }

    [Test]
    public void ComputeHistoryMarker_changes_when_a_scan_document_appears_or_disappears()
    {
        var doc = TestDoc("rev-2", ("rev-1", T0), ("rev-2", T1));
        var full = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Pending,
            ["rev-2"] = SecurityStatus.Verified
        };
        var neverScanned = new Dictionary<string, SecurityStatus> { ["rev-2"] = SecurityStatus.Verified };

        Assert.That(GitRepositoryCache.ComputeHistoryMarker(doc, true, full),
            Is.Not.EqualTo(GitRepositoryCache.ComputeHistoryMarker(doc, true, neverScanned)));
    }

    [Test]
    public void ComputeHistoryMarker_changes_when_security_is_toggled()
    {
        var doc = TestDoc("rev-1", ("rev-1", T0));
        var statuses = new Dictionary<string, SecurityStatus> { ["rev-1"] = SecurityStatus.Verified };

        Assert.That(GitRepositoryCache.ComputeHistoryMarker(doc, true, statuses),
            Is.Not.EqualTo(GitRepositoryCache.ComputeHistoryMarker(doc, false, null)));
    }

    [Test]
    public void ComputeHistoryMarker_changes_when_history_ages_out_a_revision()
    {
        var withOldRevision = TestDoc("rev-2", ("rev-1", T0), ("rev-2", T1));
        var withoutOldRevision = TestDoc("rev-2", ("rev-2", T1));
        var statuses = new Dictionary<string, SecurityStatus>
        {
            ["rev-1"] = SecurityStatus.Verified,
            ["rev-2"] = SecurityStatus.Verified
        };

        Assert.That(GitRepositoryCache.ComputeHistoryMarker(withOldRevision, true, statuses),
            Is.Not.EqualTo(GitRepositoryCache.ComputeHistoryMarker(withoutOldRevision, true, statuses)),
            "aging out an old revision must invalidate the marker even when the head is unchanged");
    }

    private static PackageDocument TestDoc(string headRevisionId, params (string Id, DateTimeOffset At)[] revisions)
    {
        return new PackageDocument
        {
            Id = "pkg",
            PackageName = "pkg",
            CreatedAt = T0,
            UpdatedAt = T2,
            HeadRevisionId = headRevisionId,
            Revisions = revisions
                .Select(r => new PackageRevisionDocument { RevisionId = r.Id, CreatedAt = r.At })
                .ToList()
        };
    }
}
