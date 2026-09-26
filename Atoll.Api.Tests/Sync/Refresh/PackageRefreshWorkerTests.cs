using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Sync.Refresh;

public class PackageRefreshWorkerTests
{
    private static readonly IReadOnlyDictionary<string, string> BaseFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
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

    [Fact]
    public async Task RunCycleAsync_ChangedUpstreamHead_AppendsRevision()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                // Provide different file content so a new revision ID is computed.
                ["shelly"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        var outcome = await worker.RunCycleAsync(CancellationToken.None);
        var newHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            Assert.Equal(RefreshCycleOutcome.Completed, outcome);
            Assert.NotEqual(originalHead, newHead, StringComparer.Ordinal);
            Assert.Equal(1, mirror.FetchedBatches.Sum(b => b.Count));
            Assert.Equal(1, status.GetSnapshot().PackagesUpdated);
            Assert.Equal(0, status.GetSnapshot().PackagesUnchanged);
        });
    }

    [Fact]
    public async Task RunCycleAsync_NewHead_MarksPendingAndDemotesOldHeadScan()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;
        // Simulate the old head being scanned clean.
        await security.ScanRevisionAsync("shelly", originalHead, SecurityStatus.Verified);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), new RefreshStatusStore(true));

        await worker.RunCycleAsync(CancellationToken.None);

        var newHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;
        var newHeadScan = await security.GetAsync("shelly", newHead, TestContext.Current.CancellationToken);
        var oldHeadScan = await security.GetAsync("shelly", originalHead, TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(SecurityStatus.Pending, newHeadScan!.Status);
            Assert.True(newHeadScan.IsHead);
            // Old head scan is demoted from the head slot but its verdict is preserved.
            Assert.False(oldHeadScan!.IsHead);
            Assert.Equal(SecurityStatus.Verified, oldHeadScan.Status);
        });
    }

    [Fact]
    public async Task RunCycleAsync_UnchangedUpstreamHead_SkipsFetch()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        // Pre-seed the sync watermark so the package looks already synced.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null, TestContext.Current.CancellationToken);

        var mirror = new FakeRefreshMirror { BranchHeads = { ["shelly"] = "sha-stable" } };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Empty(mirror.FetchedBatches);
            Assert.Equal(0, status.GetSnapshot().PackagesUpdated);
        });
    }

    [Fact]
    public async Task RunCycleAsync_SplitPackageMembers_FansOutOneFetchToEveryMember()
    {
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "libfoo", BaseFiles);
        await SeedAsync(service, "libfoo-devel", BaseFiles);

        var libfooOriginalHead = (await repo.GetHeadAsync("libfoo", TestContext.Current.CancellationToken))!.HeadRevisionId;
        var libfooDevelOriginalHead = (await repo.GetHeadAsync("libfoo-devel", TestContext.Current.CancellationToken))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["foo"] = "sha-new" },
            FilesFor =
            {
                ["foo"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var libfooUpdated = (await repo.GetHeadAsync("libfoo", TestContext.Current.CancellationToken))!.HeadRevisionId;
        var libfooDevelUpdated = (await repo.GetHeadAsync("libfoo-devel", TestContext.Current.CancellationToken))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            // One pkgbase fetched, both members updated.
            Assert.Equal(1, mirror.FetchedBatches.Sum(b => b.Count));
            // Revision IDs are pkgname-scoped, so they differ across members but both must change.
            Assert.NotEqual(libfooOriginalHead, libfooUpdated, StringComparer.Ordinal);
            Assert.NotEqual(libfooDevelOriginalHead, libfooDevelUpdated, StringComparer.Ordinal);
            Assert.Equal(2, status.GetSnapshot().PackagesUpdated);
        });
    }

    [Fact]
    public async Task RunCycleAsync_PartialFailure_AdvancesWatermarkOnlyForSucceededMembers()
    {
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "libfoo", BaseFiles);
        await SeedAsync(service, "libfoo-devel", BaseFiles);

        var libfooOriginalHead = (await repo.GetHeadAsync("libfoo", TestContext.Current.CancellationToken))!.HeadRevisionId;

        // Make libfoo-devel's refresh fail by deleting its document mid-flight (e.g. a concurrent delete).
        await repo.DeleteAsync("libfoo-devel", TestContext.Current.CancellationToken);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["foo"] = "sha-new" },
            FilesFor =
            {
                ["foo"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var libfooUpdated = (await repo.GetHeadAsync("libfoo", TestContext.Current.CancellationToken))!.HeadRevisionId;
        var libfooDoc = await repo.GetHeadAsync("libfoo", TestContext.Current.CancellationToken);
        var libfooState = (await repo.ListSyncStatesAsync(TestContext.Current.CancellationToken)).Single(s => string.Equals(s.PackageName, "libfoo", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.NotEqual(libfooOriginalHead, libfooUpdated, StringComparer.Ordinal);
            Assert.Equal(1, status.GetSnapshot().PackagesUpdated);
            // The succeeded member's watermark advances so it won't be refetched next cycle.
            Assert.Equal("sha-new", libfooState.LastSyncedUpstreamHead);
            Assert.Null(libfooDoc!.LastSyncError);
        });
    }

    [Fact]
    public async Task RunCycleAsync_SameContentRevision_NoOpsButAdvancesWatermark()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

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

        var newHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;
        var state = (await repo.ListSyncStatesAsync(TestContext.Current.CancellationToken)).Single();

        Assert.Multiple(() =>
        {
            Assert.Equal(originalHead, newHead);
            Assert.Equal(1, status.GetSnapshot().PackagesUnchanged);
            Assert.Equal(0, status.GetSnapshot().PackagesUpdated);
            // Watermark still advances so we don't refetch next cycle.
            Assert.Equal("sha-moved-but-same-content", state.LastSyncedUpstreamHead);
        });
    }

    [Fact]
    public async Task RunCycleAsync_PkgBaseMissingFromMirror_SkipsAndRecordsStatus()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("ghost", "ghost"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);
        await SeedAsync(service, "ghost", BaseFiles);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>(StringComparer.Ordinal)
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
            Assert.Equal(1, snapshot.RefsSkipped);
            Assert.Equal(1, snapshot.PackagesUpdated);
        });
    }

    [Fact]
    public async Task RunCycleAsync_FailedRefs_IsolatesAfterBisectionAndContinues()
    {
        var store = IndexWithPackages(Meta("good", "good"), Meta("broken", "broken"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "good", BaseFiles);
        await SeedAsync(service, "broken", BaseFiles);

        var goodOriginalHead = (await repo.GetHeadAsync("good", TestContext.Current.CancellationToken))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["good"] = "sha-new", ["broken"] = "sha-new" },
            FetchFails = { "broken" },
            FilesFor =
            {
                ["good"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, new InMemorySeedExclusionRepository(), status);

        await worker.RunCycleAsync(CancellationToken.None);

        var goodNewHead = (await repo.GetHeadAsync("good", TestContext.Current.CancellationToken))!.HeadRevisionId;
        Assert.Multiple(() =>
        {
            Assert.NotEqual(goodOriginalHead, goodNewHead, StringComparer.Ordinal);
            Assert.Equal(1, status.GetSnapshot().PackagesUpdated);
            Assert.Equal(1, status.GetSnapshot().RefsFailed);
            Assert.Equal(1, status.GetSnapshot().PackagesSkipped);
        });
    }

    [Fact]
    public async Task SelectCandidates_StalePackageWithUnchangedHead_StillSelectsCandidate()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        // Mark synced with the current head but a long-since past success timestamp.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null, TestContext.Current.CancellationToken);

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

        var states = await repo.ListSyncStatesAsync(TestContext.Current.CancellationToken);
        var grouped = RefreshPlan.GroupByPackageBase([.. states], store.Current);
        var candidates = RefreshPlan.SelectCandidates(
            grouped,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["shelly"] = "sha-stable" },
            DateTimeOffset.UtcNow.AddHours(2),
            TimeSpan.FromHours(1));

        Assert.Single(candidates);
    }

    [Fact]
    public async Task SelectCandidates_StaleUnchangedHead_MarksNoFetch()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);
        // Synced against the current head; only staleness will make it a candidate.
        await repo.UpdateSyncStateAsync(["shelly"], "sha-stable", true, null, TestContext.Current.CancellationToken);

        var states = await repo.ListSyncStatesAsync(TestContext.Current.CancellationToken);
        var grouped = RefreshPlan.GroupByPackageBase([.. states], store.Current);
        var candidates = RefreshPlan.SelectCandidates(
            grouped,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["shelly"] = "sha-stable" },
            DateTimeOffset.UtcNow.AddHours(2),
            TimeSpan.FromHours(1));

        Assert.Multiple(() =>
        {
            var candidate = Assert.Single(candidates);
            // Head unchanged -> worker must advance watermark without fetching.
            Assert.True(candidate.HeadUnchanged);
        });
    }

    [Fact]
    public async Task RunCycleAsync_MaxPackagesPerRun_CapsCandidateSelection()
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
        var service = new PackageService(repo, Options.Create(opts), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(opts), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
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
            Assert.Equal(2, mirror.FetchedBatches.Sum(b => b.Count));
            Assert.Equal(2, status.GetSnapshot().CandidatePackages);
        });
    }

    [Fact]
    public async Task RunCycleAsync_ManyChangedPkgBases_RefreshesAllAcrossBatches()
    {
        var metas = Enumerable.Range(0, 25)
            .Select(i => Meta($"pkg-{i}", $"base-{i}"))
            .ToArray();
        var store = IndexWithPackages(metas);
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        foreach (var meta in metas)
            await SeedAsync(service, meta.Name, BaseFiles);

        var updatedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
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
            Assert.Equal(RefreshCycleOutcome.Completed, outcome);
            Assert.Equal(3, mirror.FetchedBatches.Count);
            Assert.Equal(25, status.GetSnapshot().PackagesUpdated);
            Assert.Equal(0, status.GetSnapshot().PackagesUnchanged);
            Assert.Equal(0, status.GetSnapshot().PackagesSkipped);
        });
    }

    [Fact]
    public void GroupByPackageBase_IndexHasNoEntry_FallsBackToPkgName()
    {
        // A seeded package that the current dump no longer describes still has to group somewhere;
        // the pkgname is the only remaining key, and it is what upstream names map to by definition.
        var index = SearchIndexData.Empty;
        var state = new PackageSyncState { PackageName = "orphan" };

        var grouped = RefreshPlan.GroupByPackageBase([state], index);

        Assert.Equal("orphan", grouped.Keys.Single());
    }

    [Fact]
    public async Task RunCycleAsync_OversizedRevisionSnapshot_RecordsExclusionAndSkipsNextCycle()
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
        var service = new PackageService(repo, Options.Create(opts), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(opts), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                // Each file fits within MaxFileBytes, but together they push the revision
                // snapshot past MongoDB's 16 MiB document limit.
                ["shelly"] = new Dictionary<string, string>(StringComparer.Ordinal)
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

        var docAfterFirstCycle = await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken);
        var excludedBases = await exclusions.ListDocumentTooLargePackageBasesAsync(TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(originalHead, docAfterFirstCycle!.HeadRevisionId);
            Assert.Contains("shelly", excludedBases);
            Assert.NotNull(docAfterFirstCycle.LastSyncError);
            Assert.Contains("exceeds", docAfterFirstCycle.LastSyncError, StringComparison.Ordinal);
            Assert.Single(mirror.FetchedBatches);
        });

        // The excluded pkgbase is removed from the candidate set before any fetch,
        // so a second cycle must not fetch it again.
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Single(mirror.FetchedBatches);
    }

    [Fact]
    public async Task RunCycleAsync_DocumentTooLargeExclusion_SkipsWithoutFetching()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, Options.Create(EnabledOptions()), security, new PkgBuildSecurityScanner(), new GitRepositoryCache(repo, security, Options.Create(EnabledOptions()), NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await SeedAsync(service, "shelly", BaseFiles);

        var originalHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("shelly", ["shelly"], 17_000_000, TestContext.Current.CancellationToken);

        var mirror = new FakeRefreshMirror
        {
            BranchHeads = { ["shelly"] = "sha-new" },
            FilesFor =
            {
                ["shelly"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PKGBUILD"] = "pkgname=demo\npkgver=2.0\n",
                    [".SRCINFO"] = "pkgname = demo\n"
                }
            }
        };
        var status = new RefreshStatusStore(true);
        var worker = CreateWorker(store, repo, service, mirror, exclusions, status);

        await worker.RunCycleAsync(CancellationToken.None);

        var newHead = (await repo.GetHeadAsync("shelly", TestContext.Current.CancellationToken))!.HeadRevisionId;

        Assert.Multiple(() =>
        {
            Assert.Empty(mirror.FetchedBatches);
            Assert.Equal(originalHead, newHead);
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