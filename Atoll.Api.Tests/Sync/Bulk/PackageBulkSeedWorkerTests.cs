using Atoll.Api.Services.Security;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Sync.Bulk;

public class PackageBulkSeedWorkerTests
{
    private static readonly IReadOnlyDictionary<string, string> BaseFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PKGBUILD"] = "pkgname=demo\npkgver=1.0\n",
            [".SRCINFO"] = "pkgname = demo\n"
        };

    private static AtollOptions BulkOptions()
    {
        return new AtollOptions
        {
            Seed = new SeedOptions
            {
                Mode = SeedMode.Bulk,
                Bulk = new BulkSeedOptions
                {
                    BatchSize = 10,
                    BatchDelayMs = 1000,
                    AurFallbackForNotOnMirror = false
                }
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

    private static PackageBulkSeedWorker CreateWorker(
        PackageIndexStore store,
        IPackageRepository repo,
        FakeMirror mirror,
        BulkSeedStatusStore status,
        ISeedExclusionRepository? exclusions = null)
    {
        var options = Options.Create(BulkOptions());
        var security = new InMemoryPackageSecurityRepository();
        var cache = new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance);
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(), cache, TestHybridCache.New(), store);
        var seeder = new DirectPackageSeeder(repo, store, new AurGitPackageSource(), service);
        exclusions ??= new InMemorySeedExclusionRepository();
        return new PackageBulkSeedWorker(
            store,
            repo,
            service,
            seeder,
            exclusions,
            mirror,
            status,
            options,
            NullLogger<PackageBulkSeedWorker>.Instance);
    }

    [Fact]
    public async Task RunCycleAsync_NonSplitPackage_SeedsFromMirrorFiles()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "shelly" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var shellySeeded = await repo.ExistsAsync("shelly", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded);
            Assert.Equal(0, skipped);
            Assert.True(shellySeeded);
            Assert.Single(mirror.FetchedBatches);
        });
    }

    [Fact]
    public async Task RunCycleAsync_SplitPkgNamesSharingBase_FetchesPkgBaseOnceAndFansOut()
    {
        // libfoo + libfoo-devel share pkgbase "foo": one fetch, two seeds, identical files.
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "foo" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var libfooSeeded = await repo.ExistsAsync("libfoo", TestContext.Current.CancellationToken);
        var libfooDevelSeeded = await repo.ExistsAsync("libfoo-devel", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(2, seeded);
            Assert.True(libfooSeeded);
            Assert.True(libfooDevelSeeded);
            // Only one pkgbase fetched despite two pkgnames.
            Assert.Equal(1, mirror.FetchedBatches.Sum(b => b.Count));
        });
    }

    [Fact]
    public async Task RunCycleAsync_PkgBaseMissingFromMirror_SkipsAndRecordsInStatus()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("ghost", "ghost"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "shelly" } }; // "ghost" has no branch
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status);

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var shellySeeded = await repo.ExistsAsync("shelly", TestContext.Current.CancellationToken);
        var ghostSeeded = await repo.ExistsAsync("ghost", TestContext.Current.CancellationToken);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded);
            Assert.Equal(1, skipped);
            Assert.True(shellySeeded);
            Assert.False(ghostSeeded);
            Assert.Equal(1, snapshot.RefsSkipped);
            Assert.Equal(1, snapshot.PackagesSkipped);
        });
    }

    [Fact]
    public async Task RunCycleAsync_DocumentTooLargeExclusion_IsNotRefetched()
    {
        var store = IndexWithPackages(
            Meta("duckstation", "duckstation"),
            Meta("duckstation-gpl", "duckstation"),
            Meta("small-package", "small-package"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "duckstation", "small-package" } };
        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("duckstation", ["duckstation", "duckstation-gpl"], 21_957_167, TestContext.Current.CancellationToken);
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status, exclusions);

        var (seeded, skipped, backedOff) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded);
            Assert.Equal(0, skipped);
            Assert.False(backedOff);
            Assert.Equivalent(new[] { "small-package" }, mirror.FetchedBatches.SelectMany(x => x), strict: true);
            Assert.Equal(2, status.GetSnapshot().PackagesExcluded);
        });
    }

    [Fact]
    public async Task RunCycleAsync_FailedRefsAfterBisection_ReportsInStatus()
    {
        var store = IndexWithPackages(Meta("good", "good"), Meta("broken", "broken"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror
        {
            Branches = { "good", "broken" },
            FetchFails = { "broken" }
        };
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status);

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded);
            Assert.Equal(1, skipped);
            Assert.Equal(1, status.GetSnapshot().RefsFailed);
        });
    }

    [Fact]
    public async Task RunCycleAsync_AlreadySeededPackage_IsSkipped()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("other", "other"));
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(BulkOptions());
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, options, security, new PkgBuildSecurityScanner(),
            new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance), TestHybridCache.New(), store);
        await service.SeedFilesAsync("shelly", BaseFiles); // pre-seeded

        var mirror = new FakeMirror { Branches = { "shelly", "other" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded);
            Assert.Equal(1, mirror.FetchedBatches.Sum(b => b.Count));
            Assert.Equivalent(new[] { "other" }, mirror.FetchedBatches[0], strict: true);
        });
    }

    [Fact]
    public async Task RunCycleAsync_EmptyIndex_SeedsNothing()
    {
        var store = new PackageIndexStore(); // empty
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror();
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(0, seeded);
            Assert.Empty(mirror.FetchedBatches);
        });
    }

    [Fact]
    public void GetSnapshot_DisabledStatusStore_ReportsZeros()
    {
        var store = new BulkSeedStatusStore(false);

        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.False(snapshot.Enabled);
            Assert.Equal(0, snapshot.BatchesAttempted);
            Assert.Equal(0, snapshot.PackagesSeeded);
        });
    }

    [Fact]
    public async Task RunCycleAsync_FortyPackagesInBatchesOfTen_SeedsAllAcrossFourBatches()
    {
        var metas = Enumerable.Range(0, 40)
            .Select(i => Meta($"pkg-{i}", $"base-{i}"))
            .ToArray();
        var store = IndexWithPackages(metas);
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror();
        foreach (var meta in metas)
            mirror.Branches.Add(meta.PackageBase);
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status);

        // Batch size 10 forces four fetch batches; default parallelism seeds them concurrently.
        var (seeded, skipped, backedOff) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(40, seeded);
            Assert.Equal(0, skipped);
            Assert.False(backedOff);
            Assert.Equal(4, mirror.FetchedBatches.Count);
            Assert.Equal(40, status.GetSnapshot().PackagesSeeded);
        });

        foreach (var meta in metas)
            Assert.True(await repo.ExistsAsync(meta.Name, TestContext.Current.CancellationToken), $"package {meta.Name} should be seeded");
    }

    [Fact]
    public async Task RunCycleAsync_ReadFilesFailure_DoesNotKillCycle()
    {
        var store = IndexWithPackages(Meta("a", "a"), Meta("b", "b"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FailingReadMirror(["b"]);
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var aSeeded = await repo.ExistsAsync("a", TestContext.Current.CancellationToken);
        var bSeeded = await repo.ExistsAsync("b", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(1, seeded); // "a" still seeds
            Assert.True(aSeeded);
            Assert.False(bSeeded);
            Assert.Equal(1, skipped);
        });
    }

    private class FakeMirror : IAurMirror
    {
        public HashSet<string> Branches { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FetchFails { get; } = new(StringComparer.Ordinal);
        private static IReadOnlyDictionary<string, string> Files => BaseFiles;
        public List<IReadOnlyList<string>> FetchedBatches { get; } = [];

        public Task EnsureInitializedAsync(CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlySet<string>> ListBranchesAsync(CancellationToken ct = default)
        {
            IReadOnlySet<string> result = Branches;
            return Task.FromResult(result);
        }

        public Task<IReadOnlyDictionary<string, string>> ListBranchHeadsAsync(CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, string> result = Branches.ToDictionary(
                b => b,
                _ => "sha-placeholder",
                StringComparer.Ordinal);
            return Task.FromResult(result);
        }

        public Task<BulkFetchResult> FetchAsync(IReadOnlyList<string> pkgBases, CancellationToken ct = default)
        {
            FetchedBatches.Add(pkgBases);
            var succeeded = pkgBases.Where(b => !FetchFails.Contains(b)).ToList();
            var failed = pkgBases.Where(FetchFails.Contains).ToList();
            return Task.FromResult(new BulkFetchResult(succeeded, failed));
        }

        public virtual Task<IReadOnlyDictionary<string, string>> ReadFilesAsync(string pkgBase, CancellationToken ct = default)
        {
            var result = Files;
            return Task.FromResult(result);
        }
    }

    private sealed class InMemorySeedExclusionRepository : ISeedExclusionRepository
    {
        private readonly Lock _gate = new();
        private readonly HashSet<string> _packageBases = new(StringComparer.Ordinal);

        public Task<IReadOnlySet<string>> ListDocumentTooLargePackageBasesAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                IReadOnlySet<string> result = new HashSet<string>(_packageBases, StringComparer.Ordinal);
                return Task.FromResult(result);
            }
        }

        public Task RecordDocumentTooLargeAsync(
            string packageBase,
            IReadOnlyList<string> packageNames,
            long serializedSizeBytes,
            CancellationToken ct = default)
        {
            lock (_gate)
            {
                _packageBases.Add(packageBase);
                return Task.CompletedTask;
            }
        }
    }

    private sealed class FailingReadMirror : FakeMirror
    {
        private readonly HashSet<string> _readFails;

        public FailingReadMirror(IEnumerable<string> readFails)
        {
            Branches.Add("a");
            Branches.Add("b");
            _readFails = new HashSet<string>(readFails, StringComparer.Ordinal);
        }

        public override Task<IReadOnlyDictionary<string, string>> ReadFilesAsync(
            string pkgBase,
            CancellationToken ct = default)
        {
            if (_readFails.Contains(pkgBase))
                throw new InvalidOperationException("read failed");

            return base.ReadFilesAsync(pkgBase, ct);
        }
    }
}