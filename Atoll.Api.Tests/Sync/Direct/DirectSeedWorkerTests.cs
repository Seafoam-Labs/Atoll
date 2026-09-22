using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
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

        public Task<PackageIndexResponse> GetIndexPageAsync(
            int page,
            int limit,
            PackageIndexSortBy sortBy = PackageIndexSortBy.Name,
            PackageIndexSortOrder? order = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => Task.FromResult(seededNames.Contains(packageName, StringComparer.Ordinal) || SeedCalls.Contains(packageName));

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

        public async Task<IReadOnlyDictionary<string, string>> FetchFilesAsync(
            string packageBase, CancellationToken ct = default)
        {
            if (CancelDuringFetch is not null)
                await CancelDuringFetch.CancelAsync();

            if (FailFor.Contains(packageBase))
                throw new InvalidOperationException("boom");

            return new Dictionary<string, string>(StringComparer.Ordinal) { ["PKGBUILD"] = "pkgname=x\n" };
        }
    }

    /// <summary>Serves ListAsync with a fixed set of already-seeded names.</summary>
    private sealed class FakePackageRepository(IReadOnlyList<string> existing) : IPackageRepository
    {
        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
            => Task.FromResult(existing);

        public Task<long> CountAsync(CancellationToken ct = default)
            => Task.FromResult((long)existing.Count);

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexPageAsync(int skip, int take, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageIndexEntry>> ListIndexEntriesAsync(
            IReadOnlyCollection<string> names, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => Task.FromResult(existing.Contains(packageName, StringComparer.Ordinal));

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

    [Fact]
    public async Task RunCycleAsync_skips_when_index_is_empty()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var worker = CreateWorker(
            IndexWithPackages(), new FakePackageRepository([]), new FakeAurPackageSource(), new FakeSeedService([]), status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        var snapshot = status.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.Equal(DirectSeedCycleOutcome.IndexEmpty, result.Outcome);
            Assert.Equal(0, snapshot.CyclesStarted);
        });
    }

    [Fact]
    public async Task RunCycleAsync_skips_when_nothing_is_missing()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        var service = new FakeSeedService([]);
        var worker = CreateWorker(IndexWithPackages(Meta("shelly")), new FakePackageRepository(["shelly"]), new FakeAurPackageSource(), service, status);

        var result = await worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(DirectSeedCycleOutcome.NothingMissing, result.Outcome);
            Assert.Empty(service.SeedCalls);
            Assert.Equal(0, status.GetSnapshot().CyclesStarted);
        });
    }

    [Fact]
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
            Assert.Equal(DirectSeedCycleOutcome.Completed, result.Outcome);
            Assert.Equal(2, result.Seeded);
            Assert.Equivalent(new[] { "one", "two" }, service.SeedCalls, strict: true);
            Assert.Equal(1, snapshot.CyclesStarted);
            Assert.Equal(1, snapshot.CyclesCompleted);
            Assert.Equal(2, snapshot.Candidates);
            Assert.Equal(2, snapshot.Seeded);
            Assert.Equal(0, snapshot.Failed);
            Assert.Equal(0, snapshot.AlreadyPresent);
            Assert.NotNull(snapshot.LastStartedUtc);
            Assert.NotNull(snapshot.LastFinishedUtc);
        });
    }

    [Fact]
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
            Assert.Equal(DirectSeedCycleOutcome.Completed, result.Outcome);
            Assert.Equal(1, result.Seeded);
            Assert.Equal(new[] { "fine" }, service.SeedCalls, StringComparer.Ordinal);
            Assert.Equal(1, snapshot.Seeded);
            Assert.Equal(1, snapshot.Failed);
            Assert.Equal(1, snapshot.CyclesCompleted);
        });
    }

    [Fact]
    public async Task RunCycleAsync_ends_cycle_even_when_cancelled_midway()
    {
        var status = new DirectSeedStatusStore(enabled: true);
        using var cts = new CancellationTokenSource();
        // The first seed succeeds and requests cancellation; the inter-package
        // Task.Delay then throws and the finally block still ends the cycle.
        var source = new FakeAurPackageSource { CancelDuringFetch = cts };
        var worker = CreateWorker(
            IndexWithPackages(Meta("one"), Meta("two")), new FakePackageRepository([]), source, new FakeSeedService([]), status);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.RunCycleAsync(TimeSpan.FromMilliseconds(1), cts.Token));

        // BeginCycle ran, EndCycle ran in finally: counters stay paired even on cancellation.
        var snapshot = status.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.Equal(1, snapshot.CyclesStarted);
            Assert.Equal(1, snapshot.CyclesCompleted);
            Assert.Equal(1, snapshot.Seeded);
        });
    }
}
