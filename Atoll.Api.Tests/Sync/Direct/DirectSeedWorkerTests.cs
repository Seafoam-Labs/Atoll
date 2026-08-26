using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Sync.Direct;

public class DirectSeedWorkerTests
{
    /// <summary>Records persisted seeds; fetch failures are simulated on the source.</summary>
    private sealed class FakeSeedService(IReadOnlyList<string> seededNames) : IPackageService
    {
        public List<string> SeedCalls { get; } = [];

        public Task<IReadOnlyList<string>> ListAsync()
        {
            IReadOnlyList<string> result = [.. seededNames, .. SeedCalls];
            return Task.FromResult(result);
        }

        public Task<int> CountAsync()
            => Task.FromResult(seededNames.Count + SeedCalls.Count);

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => Task.FromResult(seededNames.Contains(packageName) || SeedCalls.Contains(packageName));

        public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
        {
            SeedCalls.Add(packageName);
            return Task.CompletedTask;
        }

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Records fetched pkgbases; fails or cancels for the configured packages.</summary>
    private sealed class FakeAurPackageSource : IAurPackageSource
    {
        public HashSet<string> FailFor { get; } = [];

        public CancellationTokenSource? CancelDuringFetch { get; set; }

        public Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(
            string packageBase, CancellationToken ct = default)
        {
            if (CancelDuringFetch is not null)
                CancelDuringFetch.Cancel();

            return FailFor.Contains(packageBase)
                ? Task.FromException<IReadOnlyDictionary<string, string>>(new InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string> { ["PKGBUILD"] = "pkgname=x\n" });
        }
    }

    /// <summary>Serves ListAsync with a fixed set of already-seeded names.</summary>
    private sealed class FakePackageRepository(IReadOnlyList<string> existing) : IPackageRepository
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(existing);

        public Task<long> CountAsync(CancellationToken ct = default)
            => Task.FromResult((long)existing.Count);

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => Task.FromResult(existing.Contains(packageName));

        public Task<PackageDocument?> GetHeadAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string?> GetHeadRevisionIdAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PackageRevisionContentDocument?> GetRevisionAsync(
            string packageName, string revisionId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(
            string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task InsertSeedAsync(
            PackageDocument doc, PackageRevisionContentDocument revision, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task AppendRevisionAsync(
            string packageName,
            PackageRevisionContentDocument revision,
            int maxRevisions,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageSyncState>> ListSyncStatesAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UpdateSyncStateAsync(
            IReadOnlyCollection<string> packageNames,
            string? upstreamHead,
            bool succeeded,
            string? error,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static AurPackageMetadata Meta(string name)
    {
        return new AurPackageMetadata(0, name, 0, name, "1.0", "d", null, 0, 0, null, null, null, 0, 0, "",
            [], [], [], [], [], [], [], []);
    }

    private static PackageIndexStore IndexWithPackages(params AurPackageMetadata[] packages)
    {
        var store = new PackageIndexStore();
        store.Replace(PackageIndexBuilder.BuildFromPackages(packages));
        return store;
    }

    private static DirectSeedWorker CreateWorker(
        PackageIndexStore store,
        IPackageRepository repo,
        FakeAurPackageSource source,
        IPackageService service,
        DirectSeedStatusStore status)
    {
        var seeder = new DirectPackageSeeder(repo, store, source, service);
        return new DirectSeedWorker(
            store,
            repo,
            seeder,
            status,
            Options.Create(new AtollOptions()),
            NullLogger<DirectSeedWorker>.Instance);
    }

    [Test]
    public async Task RunCycleAsync_skips_when_index_is_empty()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var worker = CreateWorker(
            IndexWithPackages(), new FakePackageRepository([]), new FakeAurPackageSource(), new FakeSeedService([]), status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectSeedCycleOutcome.IndexEmpty));
            Assert.That(snapshot.CyclesStarted, Is.Zero);
        });
    }

    [Test]
    public async Task RunCycleAsync_skips_when_nothing_is_missing()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var service = new FakeSeedService([]);
        var worker = CreateWorker(IndexWithPackages(Meta("shelly")), new FakePackageRepository(["shelly"]), new FakeAurPackageSource(), service, status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectSeedCycleOutcome.NothingMissing));
            Assert.That(service.SeedCalls, Is.Empty);
            Assert.That(status.GetSnapshot().CyclesStarted, Is.Zero);
        });
    }

    [Test]
    public async Task RunCycleAsync_seeds_missing_and_records_status()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var service = new FakeSeedService([]);
        var worker = CreateWorker(
            IndexWithPackages(Meta("kept"), Meta("one"), Meta("two")),
            new FakePackageRepository(["kept"]),
            new FakeAurPackageSource(),
            service,
            status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectSeedCycleOutcome.Completed));
            Assert.That(result.Seeded, Is.EqualTo(2));
            Assert.That(service.SeedCalls, Is.EquivalentTo(["one", "two"]));
            Assert.That(snapshot.CyclesStarted, Is.EqualTo(1));
            Assert.That(snapshot.CyclesCompleted, Is.EqualTo(1));
            Assert.That(snapshot.Candidates, Is.EqualTo(2));
            Assert.That(snapshot.Seeded, Is.EqualTo(2));
            Assert.That(snapshot.Failed, Is.Zero);
            Assert.That(snapshot.AlreadyPresent, Is.Zero);
            Assert.That(snapshot.LastStartedUtc, Is.Not.Null);
            Assert.That(snapshot.LastFinishedUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task RunCycleAsync_records_failures_without_stopping_the_cycle()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var service = new FakeSeedService([]);
        var source = new FakeAurPackageSource();
        source.FailFor.Add("broken");
        var worker = CreateWorker(
            IndexWithPackages(Meta("broken"), Meta("fine")), new FakePackageRepository([]), source, service, status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectSeedCycleOutcome.Completed));
            Assert.That(result.Seeded, Is.EqualTo(1));
            Assert.That(service.SeedCalls, Is.EqualTo(["fine"]));
            Assert.That(snapshot.Seeded, Is.EqualTo(1));
            Assert.That(snapshot.Failed, Is.EqualTo(1));
            Assert.That(snapshot.CyclesCompleted, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task RunCycleAsync_ends_cycle_even_when_cancelled_midway()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        using var cts = new CancellationTokenSource();
        // The first seed succeeds and requests cancellation; the inter-package
        // Task.Delay then throws and the finally block still ends the cycle.
        var source = new FakeAurPackageSource { CancelDuringFetch = cts };
        var worker = CreateWorker(
            IndexWithPackages(Meta("one"), Meta("two")), new FakePackageRepository([]), source, new FakeSeedService([]), status);

        Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(),
            () => worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), cts.Token));

        // BeginCycle ran, EndCycle ran in finally: counters stay paired even on cancellation.
        var snapshot = status.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.CyclesStarted, Is.EqualTo(1));
            Assert.That(snapshot.CyclesCompleted, Is.EqualTo(1));
            Assert.That(snapshot.Seeded, Is.EqualTo(1));
        });
    }
}
