using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Sync.Refresh;

public class PackageRefreshWorkerTests
{
    private static readonly IReadOnlyDictionary<string, string> BaseFiles =
        new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };

    private static AtollOptions EnabledOptions()
    {
        return new AtollOptions
        {
            Refresh = new RefreshOptions
            {
                Enabled = true,
                BatchSize = 10,
                BatchDelayMs = 100,
                MaxPackagesPerRun = 100,
                MaxStalenessHours = 24
            },
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        };
    }

    private static AurPackageMetadata Meta(string name, string packageBase)
    {
        return new AurPackageMetadata(0, name, 0, packageBase, "1.0", "d", null, 0, 0, null, null, null, 0, 0, "",
            [], [], [], [], [], [], [], []);
    }

    private static PackageIndexStore IndexWithPackages(params AurPackageMetadata[] packages)
    {
        var store = new PackageIndexStore();
        store.Replace(PackageIndexBuilder.BuildFromPackages(packages));
        return store;
    }

    private static async Task SeedAsync(IPackageService service, string name, IReadOnlyDictionary<string, string> files)
    {
        await service.SeedFilesAsync(name, files);
    }

    private static PackageRefreshWorker CreateWorker(
        PackageIndexStore store,
        InMemoryPackageRepository repo,
        IPackageService service,
        FakeRefreshMirror mirror,
        InMemorySeedExclusionRepository exclusions,
        RefreshStatusStore status)
    {
        return new PackageRefreshWorker(
            store,
            repo,
            service,
            mirror,
            exclusions,
            status,
            Options.Create(EnabledOptions()),
            NullLogger<PackageRefreshWorker>.Instance);
    }

    [Test]
    public async Task RunCycleAsync_appends_revision_when_upstream_head_changes()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                // Provide different file content so a new revision ID is computed.
                ["shelly"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        var outcome = await worker.RunCycleAsync(CancellationToken.None);
        var newHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(RefreshCycleOutcome.Completed));
            Assert.That(newHead, Is.Not.EqualTo(originalHead));
            Assert.That(mirror.FetchedBatches.Sum(b => b.Count), Is.EqualTo(1));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.EqualTo(1));
            Assert.That(status.GetSnapshot().PackagesUnchanged, Is.Zero);
        });
    }

    [Test]
    public async Task RunCycleAsync_marks_new_head_pending_and_demotes_old_head_scan()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;
        await security.MarkPendingAsync("shelly", originalHead, true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        // Simulate the old head being scanned clean.
        await security.TryClaimPendingScanAsync("scanner", TimeSpan.FromMinutes(1), PkgBuildSecurityScanner.CurrentPolicyVersion);
        await security.CompleteScanAsync("shelly", originalHead, "scanner",
            new ScanResult(SecurityStatus.Verified, []), PkgBuildSecurityScanner.CurrentPolicyVersion);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), new RefreshStatusStore(true));

        await worker.RunCycleAsync(CancellationToken.None);

        var newHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;
        var newHeadScan = await security.GetAsync("shelly", newHead);
        var oldHeadScan = await security.GetAsync("shelly", originalHead);

        Assert.Multiple(() =>
        {
            Assert.That(newHeadScan!.Status, Is.EqualTo(SecurityStatus.Pending));
            Assert.That(newHeadScan.IsHead, Is.True);
            // Old head scan is demoted from the head slot but its verdict is preserved.
            Assert.That(oldHeadScan!.IsHead, Is.False);
            Assert.That(oldHeadScan.Status, Is.EqualTo(SecurityStatus.Verified));
        });
    }

    [Test]
    public async Task RunCycleAsync_skips_fetch_when_upstream_head_unchanged()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        // Pre-seed the sync watermark so the package looks already synced.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null);

        var mirror = new FakeRefreshMirror { BranchHeads = { ["shelly"] = "sha-stable" } };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(mirror.FetchedBatches, Is.Empty);
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.Zero);
        });
    }

    [Test]
    public async Task RunCycleAsync_fans_out_one_fetch_to_split_package_members()
    {
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "libfoo", BaseFiles);
        await SeedAsync(service, "libfoo-devel", BaseFiles);

        var libfooOriginalHead = (await repo.GetHeadAsync("libfoo"))!.HeadRevisionId;
        var libfooDevelOriginalHead = (await repo.GetHeadAsync("libfoo-devel"))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["foo"] = "sha-new" },
            FilesFor =
            {
                ["foo"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var libfooUpdated = (await repo.GetHeadAsync("libfoo"))!.HeadRevisionId;
        var libfooDevelUpdated = (await repo.GetHeadAsync("libfoo-devel"))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            // One pkgbase fetched, both members updated.
            Assert.That(mirror.FetchedBatches.Sum(b => b.Count), Is.EqualTo(1));
            // Revision IDs are pkgname-scoped, so they differ across members but both must change.
            Assert.That(libfooUpdated, Is.Not.EqualTo(libfooOriginalHead));
            Assert.That(libfooDevelUpdated, Is.Not.EqualTo(libfooDevelOriginalHead));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RunCycleAsync_advances_watermark_only_for_succeeded_members_on_partial_failure()
    {
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "libfoo", BaseFiles);
        await SeedAsync(service, "libfoo-devel", BaseFiles);

        var libfooOriginalHead = (await repo.GetHeadAsync("libfoo"))!.HeadRevisionId;

        // Make libfoo-devel's refresh fail by deleting its document mid-flight (e.g. a concurrent delete).
        await repo.DeleteAsync("libfoo-devel");

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["foo"] = "sha-new" },
            FilesFor =
            {
                ["foo"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var libfooUpdated = (await repo.GetHeadAsync("libfoo"))!.HeadRevisionId;
        var libfooDoc = await repo.GetHeadAsync("libfoo");
        var libfooState = (await repo.ListSyncStatesAsync()).Single(s => s.PackageName == "libfoo");

        Assert.Multiple(() =>
        {
            Assert.That(libfooUpdated, Is.Not.EqualTo(libfooOriginalHead));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.EqualTo(1));
            // The succeeded member's watermark advances so it won't be refetched next cycle.
            Assert.That(libfooState.LastSyncedUpstreamHead, Is.EqualTo("sha-new"));
            Assert.That(libfooDoc!.LastSyncError, Is.Null);
        });
    }

    [Test]
    public async Task RunCycleAsync_no_ops_when_computed_revision_matches_current_head()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        // Upstream head reports as moved, but files are identical (content hash unchanged).
        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-moved-but-same-content" },
            FilesFor =
            {
                ["shelly"] = BaseFiles
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var newHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;
        var state = (await repo.ListSyncStatesAsync()).Single();

        Assert.Multiple(() =>
        {
            Assert.That(newHead, Is.EqualTo(originalHead));
            Assert.That(status.GetSnapshot().PackagesUnchanged, Is.EqualTo(1));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.Zero);
            // Watermark still advances so we don't refetch next cycle.
            Assert.That(state.LastSyncedUpstreamHead, Is.EqualTo("sha-moved-but-same-content"));
        });
    }

    [Test]
    public async Task RunCycleAsync_skips_pkgbases_missing_from_mirror_and_records_in_status()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("ghost", "ghost"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);
        await SeedAsync(service, "ghost", BaseFiles);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var snapshot = status.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.RefsSkipped, Is.EqualTo(1));
            Assert.That(snapshot.PackagesUpdated, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_isolates_failed_refs_after_bisection_and_continues()
    {
        var store = IndexWithPackages(Meta("good", "good"), Meta("broken", "broken"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "good", BaseFiles);
        await SeedAsync(service, "broken", BaseFiles);

        var goodOriginalHead = (await repo.GetHeadAsync("good"))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["good"] = "sha-new", ["broken"] = "sha-new" },
            FetchFails = { "broken" },
            FilesFor =
            {
                ["good"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var goodNewHead = (await repo.GetHeadAsync("good"))!.HeadRevisionId;
        Assert.Multiple(() =>
        {
            Assert.That(goodNewHead, Is.Not.EqualTo(goodOriginalHead));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.EqualTo(1));
            Assert.That(status.GetSnapshot().RefsFailed, Is.EqualTo(1));
            Assert.That(status.GetSnapshot().PackagesSkipped, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_safety_sweep_includes_stale_packages_even_when_head_unchanged()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        // Mark synced with the current head but a long-since past success timestamp.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-stable" },
            FilesFor =
            {
                ["shelly"] = BaseFiles
            }
        };
        var status = new RefreshStatusStore(true);
        _ = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        var states = await repo.ListSyncStatesAsync();
        var grouped = RefreshPlan.GroupByPackageBase([.. states], store.Current);
        var candidates = RefreshPlan.SelectCandidates(
            grouped,
            new Dictionary<string, string> { ["shelly"] = "sha-stable" },
            DateTimeOffset.UtcNow.AddHours(2),
            TimeSpan.FromHours(1));

        Assert.That(candidates, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SelectCandidates_marks_stale_but_unchanged_head_as_no_fetch()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);
        // Synced against the current head; only staleness will make it a candidate.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null);

        var states = await repo.ListSyncStatesAsync();
        var grouped = RefreshPlan.GroupByPackageBase([.. states], store.Current);
        var candidates = RefreshPlan.SelectCandidates(
            grouped,
            new Dictionary<string, string> { ["shelly"] = "sha-stable" },
            DateTimeOffset.UtcNow.AddHours(2),
            TimeSpan.FromHours(1));

        Assert.Multiple(() =>
        {
            Assert.That(candidates, Has.Count.EqualTo(1));
            // Head unchanged -> worker must advance watermark without fetching.
            Assert.That(candidates[0].HeadUnchanged, Is.True);
        });
    }

    [Test]
    public async Task RunCycleAsync_caps_candidates_per_run()
    {
        var store = IndexWithPackages(Meta("a", "a"), Meta("b", "b"), Meta("c", "c"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var opts = new AtollOptions
        {
            Refresh = new RefreshOptions
            {
                Enabled = true,
                BatchSize = 10,
                BatchDelayMs = 100,
                MaxPackagesPerRun = 2,
                MaxStalenessHours = 24
            },
            Mongo = new MongoOptions { MaxFileBytes = 5_242_880, MaxRevisions = 10 }
        };
        var service = new PackageService(repo, Options.Create(opts), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(opts), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "a", BaseFiles);
        await SeedAsync(service, "b", BaseFiles);
        await SeedAsync(service, "c", BaseFiles);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["a"] = "s", ["b"] = "s", ["c"] = "s" }
        };
        var status = new RefreshStatusStore(true);
        var worker = new PackageRefreshWorker(
            store, repo, service, mirror, new InMemorySeedExclusionRepository(), status,
            Options.Create(opts), NullLogger<PackageRefreshWorker>.Instance);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            // Only two of the three pkgbases selected.
            Assert.That(mirror.FetchedBatches.Sum(b => b.Count), Is.EqualTo(2));
            Assert.That(status.GetSnapshot().CandidatePackages, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RunCycleAsync_refreshes_many_changed_pkgbases_across_batches()
    {
        var metas = Enumerable.Range(0, 25)
            .Select(i => Meta($"pkg-{i}", $"base-{i}"))
            .ToArray();
        var store = IndexWithPackages(metas);
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        foreach (var meta in metas)
            await SeedAsync(service, meta.Name, BaseFiles);

        var updatedFiles = new Dictionary<string, string>
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };
        var mirror = new FakeRefreshMirror();
        foreach (var meta in metas)
        {
            mirror.BranchHeads[meta.PackageBase] = $"sha-new-{meta.PackageBase}";
            mirror.FilesFor[meta.PackageBase] = updatedFiles;
        }

        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        // Batch size 10 forces three fetch batches; default parallelism refreshes them concurrently.
        var outcome = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(RefreshCycleOutcome.Completed));
            Assert.That(mirror.FetchedBatches, Has.Count.EqualTo(3));
            Assert.That(status.GetSnapshot().PackagesUpdated, Is.EqualTo(25));
            Assert.That(status.GetSnapshot().PackagesUnchanged, Is.Zero);
            Assert.That(status.GetSnapshot().PackagesSkipped, Is.Zero);
        });
    }

    [Test]
    public void RefreshPlan_ResolvePackageBase_falls_back_to_stored_upstream_base()
    {
        // Package not in the index but has a persisted upstream pkgbase.
        var index = SearchIndexData.Empty;
        var state = new PackageSyncState
        {
            PackageName = "orphan",
            UpstreamPackageBase = "orphan-base"
        };

        var grouped = RefreshPlan.GroupByPackageBase([state], index);

        Assert.That(grouped.Keys.Single(), Is.EqualTo("orphan-base"));
    }

    [Test]
    public async Task RunCycleAsync_records_exclusion_when_revision_snapshot_too_large()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        // EnabledOptions with a raised MaxFileBytes so individual files pass validation while
        // their combined revision snapshot still exceeds MongoDB's 16 MiB document limit.
        var opts = new AtollOptions
        {
            Refresh = new RefreshOptions
            {
                Enabled = true,
                BatchSize = 10,
                BatchDelayMs = 100,
                MaxPackagesPerRun = 100,
                MaxStalenessHours = 24
            },
            Mongo = new MongoOptions { MaxFileBytes = 10_485_760, MaxRevisions = 10 }
        };
        var service = new PackageService(repo, Options.Create(opts), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(opts), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                // Each file fits within MaxFileBytes, but together they push the revision
                // snapshot past MongoDB's 16 MiB document limit.
                ["shelly"] = new Dictionary<string, string>
                {
                    ["big-1.txt"] = new('x', 9_000_000),
                    ["big-2.txt"] = new('x', 9_000_000)
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var exclusions = new InMemorySeedExclusionRepository();
        var worker = new PackageRefreshWorker(
            store, repo, service, mirror, exclusions, status,
            Options.Create(opts), NullLogger<PackageRefreshWorker>.Instance);

        await worker.RunCycleAsync(CancellationToken.None);

        var docAfterFirstCycle = await repo.GetHeadAsync("shelly");
        var excludedBases = await exclusions.ListDocumentTooLargePackageBasesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(docAfterFirstCycle!.HeadRevisionId, Is.EqualTo(originalHead));
            Assert.That(excludedBases, Does.Contain("shelly"));
            Assert.That(docAfterFirstCycle.LastSyncError, Is.Not.Null);
            Assert.That(docAfterFirstCycle.LastSyncError, Does.Contain("exceeds"));
            Assert.That(mirror.FetchedBatches, Has.Count.EqualTo(1));
        });

        // The excluded pkgbase is removed from the candidate set before any fetch,
        // so a second cycle must not fetch it again.
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.That(mirror.FetchedBatches, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task RunCycleAsync_skips_pkgbases_with_document_too_large_exclusion_without_fetching()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance));
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("shelly", ["shelly"], 17_000_000);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, exclusions, status);

        await worker.RunCycleAsync(CancellationToken.None);

        var newHead = (await repo.GetHeadAsync("shelly"))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            Assert.That(mirror.FetchedBatches, Is.Empty);
            Assert.That(newHead, Is.EqualTo(originalHead));
        });
    }

    private sealed class FakeRefreshMirror : IAurMirror
    {
        public Dictionary<string, string> BranchHeads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FetchFails { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyDictionary<string, string>> FilesFor { get; } = new(StringComparer.Ordinal);
        public List<IReadOnlyList<string>> FetchedBatches { get; } = [];

        public Task EnsureInitializedAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> ListBranchesAsync(CancellationToken ct = default)
        {
            IReadOnlySet<string> result = new HashSet<string>(BranchHeads.Keys, StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyDictionary<string, string>> ListBranchHeadsAsync(CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, string> result =
                new Dictionary<string, string>(BranchHeads, StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<BulkFetchResult> FetchAsync(IReadOnlyList<string> pkgBases, CancellationToken ct = default)
        {
            FetchedBatches.Add(pkgBases);
            var succeeded = pkgBases.Where(b => !FetchFails.Contains(b)).ToList();
            var failed = pkgBases.Where(FetchFails.Contains).ToList();
            return Task.FromResult(new BulkFetchResult(succeeded, failed));
        }

        public Task<IReadOnlyDictionary<string, string>> ReadFilesAsync(string pkgBase, CancellationToken ct = default)
        {
            return Task.FromResult(FilesFor.TryGetValue(pkgBase, out var files) ? files : BaseFiles);
        }
    }
}