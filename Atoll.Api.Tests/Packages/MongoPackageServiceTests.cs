using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages;

public class MongoPackageServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> SampleFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=shelly\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = shelly\n"
        };

    private static MongoPackageService CreateService(
        InMemoryPackageRepository repo,
        PackageIndexStore? indexStore = null,
        IPackageSecurityRepository? securityRepository = null)
    {
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        });
        return new MongoPackageService(
            repo,
            indexStore ?? new PackageIndexStore(),
            options,
            securityRepository ?? new InMemoryPackageSecurityRepository(),
            NullLogger<MongoPackageService>.Instance);
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var files = await service.GetAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(files.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(files.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
            Assert.That(files.Files[".SRCINFO"], Is.EqualTo(SampleFiles[".SRCINFO"]));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetHistoryAsync_returns_one_revision()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(history, Has.Count.EqualTo(1));
            Assert.That(history[0].Sha, Has.Length.EqualTo(64));
            Assert.That(history[0].Author, Is.EqualTo("aur"));
            Assert.That(history[0].Message, Is.EqualTo("seed from AUR"));
        });
    }

    [Test]
    public async Task SeedFilesAsync_then_GetAsync_by_revision_sha_returns_files()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var history = await service.GetHistoryAsync("shelly");
        var sha = history[0].Sha;

        var byRevision = await service.GetAsync("shelly", sha);

        Assert.Multiple(() =>
        {
            Assert.That(byRevision.Files.Keys, Is.EquivalentTo(SampleFiles.Keys));
            Assert.That(byRevision.Files["PKGBUILD"], Is.EqualTo(SampleFiles["PKGBUILD"]));
        });
    }

    [Test]
    public void SeedFilesAsync_existing_package_returns_conflict()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.DoesNotThrowAsync(async () => await service.SeedFilesAsync("shelly", SampleFiles));

        var ex = Assert.ThrowsAsync<PackageConflictException>(async () => await service.SeedFilesAsync("shelly", SampleFiles))!;

        Assert.That(ex.PackageName, Is.EqualTo("shelly"));
    }

    [Test]
    public async Task SeedFilesAsync_oversized_file_throws()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 1_024, MaxRevisions = 10 }
        });
        var service = new MongoPackageService(
            repo,
            new PackageIndexStore(),
            options,
            new InMemoryPackageSecurityRepository(),
            NullLogger<MongoPackageService>.Instance);

        var big = new Dictionary<string, string>
        {
            ["big.bin"] = new('x', 2_048)
        };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await service.SeedFilesAsync("big-pkg", big))!;

        Assert.That(ex.Message, Does.Contain("big.bin"));
        Assert.That(await repo.ExistsAsync("big-pkg"), Is.False);
    }

    [Test]
    public async Task SeedFilesAsync_document_larger_than_mongo_limit_throws_typed_exception_before_insert()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new MongoPackageService(
            repo,
            new PackageIndexStore(),
            options,
            new InMemoryPackageSecurityRepository(),
            NullLogger<MongoPackageService>.Instance);
        var files = new Dictionary<string, string>
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        var ex = Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () => await service.SeedFilesAsync("too-large", files))!;
        var packageExists = await repo.ExistsAsync("too-large");

        Assert.Multiple(() =>
        {
            Assert.That(ex.PackageName, Is.EqualTo("too-large"));
            Assert.That(ex.SerializedSizeBytes, Is.GreaterThan(ex.MaxDocumentSizeBytes));
            Assert.That(packageExists, Is.False);
        });
    }

    [Test]
    public async Task AppendRevisionFromUpstreamAsync_oversized_snapshot_throws_before_write()
    {
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        });
        var service = new MongoPackageService(
            repo,
            new PackageIndexStore(),
            options,
            new InMemoryPackageSecurityRepository(),
            NullLogger<MongoPackageService>.Instance);
        await service.SeedFilesAsync("pkg", SampleFiles);
        var history = await service.GetHistoryAsync("pkg");
        var originalHead = history[0].Sha;
        var oversizedFiles = new Dictionary<string, string>
        {
            ["large-1.txt"] = new('x', 9_000_000),
            ["large-2.txt"] = new('x', 9_000_000)
        };

        Assert.ThrowsAsync<PackageDocumentTooLargeException>(async () =>
            await service.AppendRevisionFromUpstreamAsync("pkg", oversizedFiles));
        var afterHistory = await service.GetHistoryAsync("pkg");
        var packageExists = await repo.ExistsAsync("pkg");

        Assert.Multiple(() =>
        {
            Assert.That(afterHistory, Has.Count.EqualTo(1));
            Assert.That(afterHistory[0].Sha, Is.EqualTo(originalHead));
            Assert.That(packageExists, Is.True);
        });
    }

    [Test]
    public async Task DeleteAsync_then_GetAsync_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.DeleteAsync("shelly");

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly"));
    }

    [Test]
    public async Task GetAsync_unknown_package_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("missing"));
    }

    [Test]
    public async Task GetAsync_unknown_revision_throws_not_found()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);

        Assert.ThrowsAsync<KeyNotFoundException>(async () => await service.GetAsync("shelly", "deadbeef"));
    }

    [Test]
    public async Task ListAsync_returns_seeded_package_names()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        await service.SeedFilesAsync("other", SampleFiles);

        var names = await service.ListAsync();

        Assert.That(names, Is.EquivalentTo(["shelly", "other"]));
    }

    [Test]
    public async Task SeedFilesAsync_same_content_produces_same_revision_sha()
    {
        var repo = new InMemoryPackageRepository();
        var service = CreateService(repo);

        await service.SeedFilesAsync("shelly", SampleFiles);
        var firstHistory = await service.GetHistoryAsync("shelly");

        var repo2 = new InMemoryPackageRepository();
        var service2 = CreateService(repo2);
        await service2.SeedFilesAsync("shelly", SampleFiles);
        var secondHistory = await service2.GetHistoryAsync("shelly");

        Assert.That(firstHistory[0].Sha, Is.EqualTo(secondHistory[0].Sha));
    }

    [Test]
    public void ResolvePackageBase_split_package_returns_pkgbase_not_pkgname()
    {
        // Split packages (e.g. "libfoo" / "libfoo-devel" under base "foo") have
        // pkgname != pkgbase; AUR Git URLs are keyed by pkgbase.
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([
            SampleMetadata("libfoo", "foo"),
            SampleMetadata("libfoo-devel", "foo")
        ]));

        var service = CreateService(new InMemoryPackageRepository(), store);

        Assert.Multiple(() =>
        {
            Assert.That(service.ResolvePackageBase("libfoo"), Is.EqualTo("foo"));
            Assert.That(service.ResolvePackageBase("libfoo-devel"), Is.EqualTo("foo"));
        });
    }

    [Test]
    public void ResolvePackageBase_non_split_package_returns_pkgname()
    {
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([
            SampleMetadata("shelly", "shelly")
        ]));

        var service = CreateService(new InMemoryPackageRepository(), store);

        Assert.That(service.ResolvePackageBase("shelly"), Is.EqualTo("shelly"));
    }

    [Test]
    public void ResolvePackageBase_unknown_package_falls_back_to_pkgname()
    {
        // Cold start or stale index: fall back to pkgname so non-split packages
        // can still be seeded. Split packages will fail at clone time, which is
        // the pre-fix behavior and surfaces the missing index entry in logs.
        var store = new PackageIndexStore();

        var service = CreateService(new InMemoryPackageRepository(), store);

        Assert.That(service.ResolvePackageBase("anything"), Is.EqualTo("anything"));
    }

    [Test]
    public void IsRevisionServable_security_disabled_serves_every_revision()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MongoPackageService.IsRevisionServable(false, null), Is.True);
            Assert.That(MongoPackageService.IsRevisionServable(false, SecurityStatus.Pending), Is.True);
            Assert.That(MongoPackageService.IsRevisionServable(false, SecurityStatus.Verified), Is.True);
            Assert.That(MongoPackageService.IsRevisionServable(false, SecurityStatus.Flagged), Is.True);
            Assert.That(MongoPackageService.IsRevisionServable(false, SecurityStatus.Error), Is.True);
        });
    }

    [Test]
    public void IsRevisionServable_security_enabled_serves_only_verified_revisions()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MongoPackageService.IsRevisionServable(true, SecurityStatus.Verified), Is.True);
            Assert.That(MongoPackageService.IsRevisionServable(true, SecurityStatus.Pending), Is.False);
            Assert.That(MongoPackageService.IsRevisionServable(true, SecurityStatus.Flagged), Is.False);
            Assert.That(MongoPackageService.IsRevisionServable(true, SecurityStatus.Error), Is.False);
            Assert.That(MongoPackageService.IsRevisionServable(true, null), Is.False);
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

        var without = MongoPackageService.ComputeHistoryMarker(doc, false, null);
        var with = MongoPackageService.ComputeHistoryMarker(doc, false, statuses);

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

        Assert.That(MongoPackageService.ComputeHistoryMarker(doc, true, statuses),
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

        Assert.That(MongoPackageService.ComputeHistoryMarker(doc, true, flagged),
            Is.Not.EqualTo(MongoPackageService.ComputeHistoryMarker(doc, true, verified)),
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

        Assert.That(MongoPackageService.ComputeHistoryMarker(doc, true, full),
            Is.Not.EqualTo(MongoPackageService.ComputeHistoryMarker(doc, true, neverScanned)));
    }

    [Test]
    public void ComputeHistoryMarker_changes_when_security_is_toggled()
    {
        var doc = TestDoc("rev-1", ("rev-1", T0));
        var statuses = new Dictionary<string, SecurityStatus> { ["rev-1"] = SecurityStatus.Verified };

        Assert.That(MongoPackageService.ComputeHistoryMarker(doc, true, statuses),
            Is.Not.EqualTo(MongoPackageService.ComputeHistoryMarker(doc, false, null)));
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

        Assert.That(MongoPackageService.ComputeHistoryMarker(withOldRevision, true, statuses),
            Is.Not.EqualTo(MongoPackageService.ComputeHistoryMarker(withoutOldRevision, true, statuses)),
            "aging out an old revision must invalidate the marker even when the head is unchanged");
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);

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

    private static AurPackageMetadata SampleMetadata(string name, string packageBase)
    {
        return new AurPackageMetadata(
            0, name, 0, packageBase,
            "1.0", "sample", null,
            0, 0, null,
            null, null,
            0, 0, "",
            [], [], [],
            [], [], [],
            [], []);
    }
}