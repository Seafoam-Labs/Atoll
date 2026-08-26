using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Packages.Seed;

public class DirectSeedWorkerTests
{
    /// <summary>Records SeedFromAurAsync calls; fails for the configured packages.</summary>
    private sealed class FakeSeedService(IReadOnlyList<string> seededNames) : IPackageService
    {
        public List<string> SeedCalls { get; } = [];

        public HashSet<string> FailFor { get; } = [];

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


        public Task SeedFromAurAsync(string packageName)
        {
            SeedCalls.Add(packageName);
            return FailFor.Contains(packageName)
                ? Task.FromException(new InvalidOperationException("boom"))
                : Task.CompletedTask;
        }

        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetRepositoryPath(string packageName) => throw new NotSupportedException();

        public Task EnsureGitRepositoryAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Serves ListAsync with a fixed set of already-seeded names.</summary>
    private sealed class FakePackageRepository(IReadOnlyList<string> existing) : IPackageRepository
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(existing);

        public Task<long> CountAsync(CancellationToken ct = default)
            => Task.FromResult((long)existing.Count);

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

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
        store.Replace(PackageDataLoader.BuildFromPackages(packages));
        return store;
    }

    private static DirectSeedWorker CreateWorker(
        PackageIndexStore store,
        IPackageRepository repo,
        IPackageService service,
        DirectSeedStatusStore status)
    {
        return new DirectSeedWorker(
            store,
            repo,
            service,
            status,
            Options.Create(new AtollOptions()),
            NullLogger<DirectSeedWorker>.Instance);
    }

    [Test]
    public async Task RunCycleAsync_skips_when_index_is_empty()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var worker = CreateWorker(
            IndexWithPackages(), new FakePackageRepository([]), new FakeSeedService([]), status);

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
        var worker = CreateWorker(IndexWithPackages(Meta("shelly")), new FakePackageRepository(["shelly"]), service, status);

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
        service.FailFor.Add("broken");
        var worker = CreateWorker(
            IndexWithPackages(Meta("broken"), Meta("fine")), new FakePackageRepository([]), service, status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(DirectSeedCycleOutcome.Completed));
            Assert.That(result.Seeded, Is.EqualTo(1));
            Assert.That(service.SeedCalls.Count, Is.EqualTo(2));
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
        var cancelling = new CancellingSeedService(cts);
        var worker = CreateWorker(
            IndexWithPackages(Meta("one"), Meta("two")), new FakePackageRepository([]), cancelling, status);

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

    private sealed class CancellingSeedService(CancellationTokenSource cts) : IPackageService
    {
        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<int> CountAsync() => Task.FromResult(0);

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();


        public Task SeedFromAurAsync(string packageName)
        {
            cts.Cancel();
            return Task.CompletedTask;
        }

        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string GetRepositoryPath(string packageName) => throw new NotSupportedException();

        public Task EnsureGitRepositoryAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
