using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Sync.Bulk;

public class PackageBulkSeedWorkerTests
{
    private static readonly IReadOnlyDictionary<string, string> BaseFiles =
        new Dictionary<string, string>
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
        store.Replace(PackageDataLoader.BuildFromPackages(packages));
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
        var service = new PackageService(repo, options, security, cache);
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

    [Test]
    public async Task RunCycleAsync_seeds_non_split_package_from_mirror_files()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "shelly" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var shellySeeded = await repo.ExistsAsync("shelly");

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(1));
            Assert.That(skipped, Is.Zero);
            Assert.That(shellySeeded, Is.True);
            Assert.That(mirror.FetchedBatches, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_fetches_pkgbase_once_and_fans_out_to_split_pkgnames()
    {
        // libfoo + libfoo-devel share pkgbase "foo": one fetch, two seeds, identical files.
        var store = IndexWithPackages(Meta("libfoo", "foo"), Meta("libfoo-devel", "foo"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "foo" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var libfooSeeded = await repo.ExistsAsync("libfoo");
        var libfooDevelSeeded = await repo.ExistsAsync("libfoo-devel");

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(2));
            Assert.That(libfooSeeded, Is.True);
            Assert.That(libfooDevelSeeded, Is.True);
            // Only one pkgbase fetched despite two pkgnames.
            Assert.That(mirror.FetchedBatches.Sum(b => b.Count), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_skips_pkgbases_not_on_mirror_and_records_in_status()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("ghost", "ghost"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "shelly" } }; // "ghost" has no branch
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status);

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var shellySeeded = await repo.ExistsAsync("shelly");
        var ghostSeeded = await repo.ExistsAsync("ghost");
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(1));
            Assert.That(skipped, Is.EqualTo(1));
            Assert.That(shellySeeded, Is.True);
            Assert.That(ghostSeeded, Is.False);
            Assert.That(snapshot.RefsSkipped, Is.EqualTo(1));
            Assert.That(snapshot.PackagesSkipped, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_does_not_refetch_document_too_large_exclusions()
    {
        var store = IndexWithPackages(
            Meta("duckstation", "duckstation"),
            Meta("duckstation-gpl", "duckstation"),
            Meta("small-package", "small-package"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror { Branches = { "duckstation", "small-package" } };
        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("duckstation", ["duckstation", "duckstation-gpl"], 21_957_167);
        var status = new BulkSeedStatusStore(true);
        var worker = CreateWorker(store, repo, mirror, status, exclusions);

        var (seeded, skipped, backedOff) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(1));
            Assert.That(skipped, Is.Zero);
            Assert.That(backedOff, Is.False);
            Assert.That(mirror.FetchedBatches.SelectMany(x => x), Is.EquivalentTo(["small-package"]));
            Assert.That(status.GetSnapshot().PackagesExcluded, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task RunCycleAsync_reports_failed_refs_after_bisection()
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
            Assert.That(seeded, Is.EqualTo(1));
            Assert.That(skipped, Is.EqualTo(1));
            Assert.That(status.GetSnapshot().RefsFailed, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_skips_already_seeded_packages()
    {
        var store = IndexWithPackages(Meta("shelly", "shelly"), Meta("other", "other"));
        var repo = new InMemoryPackageRepository();
        var options = Options.Create(BulkOptions());
        var security = new InMemoryPackageSecurityRepository();
        var service = new PackageService(repo, options, security,
            new GitRepositoryCache(repo, security, options, NullLogger<GitRepositoryCache>.Instance));
        await service.SeedFilesAsync("shelly", BaseFiles); // pre-seeded

        var mirror = new FakeMirror { Branches = { "shelly", "other" } };
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(1));
            Assert.That(mirror.FetchedBatches.Sum(b => b.Count), Is.EqualTo(1));
            Assert.That(mirror.FetchedBatches[0], Is.EquivalentTo(["other"]));
        });
    }

    [Test]
    public async Task RunCycleAsync_empty_index_seeds_nothing()
    {
        var store = new PackageIndexStore(); // empty
        var repo = new InMemoryPackageRepository();
        var mirror = new FakeMirror();
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, _, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.Zero);
            Assert.That(mirror.FetchedBatches, Is.Empty);
        });
    }

    [Test]
    public void BulkSeedStatusStore_disabled_reports_zeros()
    {
        var store = new BulkSeedStatusStore(false);

        var snapshot = store.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Enabled, Is.False);
            Assert.That(snapshot.BatchesAttempted, Is.Zero);
            Assert.That(snapshot.PackagesSeeded, Is.Zero);
        });
    }

    [Test]
    public async Task RunCycleAsync_seeds_all_packages_in_parallel_across_batches()
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
            Assert.That(seeded, Is.EqualTo(40));
            Assert.That(skipped, Is.Zero);
            Assert.That(backedOff, Is.False);
            Assert.That(mirror.FetchedBatches, Has.Count.EqualTo(4));
            Assert.That(status.GetSnapshot().PackagesSeeded, Is.EqualTo(40));
        });

        foreach (var meta in metas)
            Assert.That(await repo.ExistsAsync(meta.Name), Is.True, $"package {meta.Name} should be seeded");
    }

    [Test]
    public async Task RunCycleAsync_handles_read_files_failure_without_killing_cycle()
    {
        var store = IndexWithPackages(Meta("a", "a"), Meta("b", "b"));
        var repo = new InMemoryPackageRepository();
        var mirror = new FailingReadMirror(["b"]);
        var worker = CreateWorker(store, repo, mirror, new BulkSeedStatusStore(true));

        var (seeded, skipped, _) = await worker.RunCycleAsync(10, TimeSpan.Zero, CancellationToken.None);

        var aSeeded = await repo.ExistsAsync("a");
        var bSeeded = await repo.ExistsAsync("b");

        Assert.Multiple(() =>
        {
            Assert.That(seeded, Is.EqualTo(1)); // "a" still seeds
            Assert.That(aSeeded, Is.True);
            Assert.That(bSeeded, Is.False);
            Assert.That(skipped, Is.EqualTo(1));
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