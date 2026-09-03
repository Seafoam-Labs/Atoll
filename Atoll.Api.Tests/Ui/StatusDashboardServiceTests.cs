using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Ui;

public class StatusDashboardServiceTests
{
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

    private static PackageIndexUpdater Updater(PackageIndexStore store)
    {
        var options = Options.Create(new AtollOptions());
        return new PackageIndexUpdater(
            store,
            new InMemoryAurMetadataRepository(),
            new AurMetadataClient(new StubHttpClientFactory(), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            new UpstreamPackageReconciler(
                new SeededNamesPackageService([]),
                options,
                NullLogger<UpstreamPackageReconciler>.Instance));
    }

    private static StatusDashboardService CreateService(
        PackageIndexStore store,
        IPackageService? packageService = null,
        InMemoryPackageSecurityRepository? security = null,
        InMemorySeedExclusionRepository? exclusions = null,
        SeedMode seedMode = SeedMode.Direct,
        bool refreshEnabled = false)
    {
        return new StatusDashboardService(
            store,
            Updater(store),
            packageService ?? new SeededNamesPackageService([]),
            security ?? new InMemoryPackageSecurityRepository(),
            exclusions ?? new InMemorySeedExclusionRepository(),
            new SecurityScanStatusStore(enabled: true),
            new DirectSeedStatusStore(seedMode == SeedMode.Direct),
            new BulkSeedStatusStore(seedMode == SeedMode.Bulk),
            new RefreshStatusStore(refreshEnabled),
            Options.Create(new AtollOptions { Seed = new SeedOptions { Mode = seedMode } }));
    }

    [Test]
    public async Task GetAsync_assembles_counts_from_all_sources()
    {
        var store = IndexWithPackages(Meta("one"), Meta("two"), Meta("three"));
        var security = new InMemoryPackageSecurityRepository();
        await security.MarkPendingAsync("one", "rev-1", true, PkgBuildSecurityScanner.CurrentPolicyVersion);
        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("big-base", ["big-base"], 20_000_000);

        var service = CreateService(
            store,
            packageService: new SeededNamesPackageService(["one"]),
            security: security,
            exclusions: exclusions);

        var model = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(model.IndexPackages, Is.EqualTo(3));
            Assert.That(model.SeededPackages, Is.EqualTo(1));
            Assert.That(model.PendingScans, Is.EqualTo(1));
            Assert.That(model.ExcludedPackageBases, Is.EqualTo(1));
            Assert.That(model.ExcludedPackageBaseNames, Is.EqualTo(["big-base"]));
            Assert.That(model.SeedMode, Is.EqualTo(SeedMode.Direct));
            Assert.That(model.DirectSeed.Enabled, Is.True);
            Assert.That(model.BulkSeed.Enabled, Is.False);
            Assert.That(model.Refresh.Enabled, Is.False);
            Assert.That(model.Security.Enabled, Is.True);
            Assert.That(model.IndexRefresh.Attempts, Is.Zero);
        });
    }

    [Test]
    public async Task GetAsync_reports_disabled_seed_and_enabled_refresh_states()
    {
        var service = CreateService(
            IndexWithPackages(Meta("one")),
            seedMode: SeedMode.Off,
            refreshEnabled: true);

        var model = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(model.SeedMode, Is.EqualTo(SeedMode.Off));
            Assert.That(model.DirectSeed.Enabled, Is.False);
            Assert.That(model.BulkSeed.Enabled, Is.False);
            Assert.That(model.Refresh.Enabled, Is.True);
            Assert.That(model.DirectSeed.LastStartedUtc, Is.Null);
            Assert.That(model.DirectSeed.LastFinishedUtc, Is.Null);
        });
    }

    [Test]
    public async Task GetAsync_returns_null_timestamps_untouched_and_orders_exclusions()
    {
        var exclusions = new InMemorySeedExclusionRepository();
        await exclusions.RecordDocumentTooLargeAsync("zeta", ["zeta"], 1);
        await exclusions.RecordDocumentTooLargeAsync("alpha", ["alpha"], 1);

        var service = CreateService(IndexWithPackages(), exclusions: exclusions);
        var model = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(model.IndexRefresh.LastStartedUtc, Is.Null);
            Assert.That(model.IndexRefresh.LastSucceededUtc, Is.Null);
            Assert.That(model.IndexRefresh.LastLoadedFromCacheUtc, Is.Null);
            Assert.That(model.Security.LastScanFinishedUtc, Is.Null);
            Assert.That(model.ExcludedPackageBaseNames, Is.EqualTo(["alpha", "zeta"]));
        });
    }

    [Test]
    public async Task GetAsync_caps_rendered_exclusions_but_reports_the_true_count()
    {
        var exclusions = new InMemorySeedExclusionRepository();
        for (var i = 0; i < StatusDashboardService.ExclusionRenderCap + 5; i++)
            await exclusions.RecordDocumentTooLargeAsync($"base-{i:D3}", [$"base-{i:D3}"], 1);

        var service = CreateService(IndexWithPackages(), exclusions: exclusions);
        var model = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(model.ExcludedPackageBases, Is.EqualTo(StatusDashboardService.ExclusionRenderCap + 5));
            Assert.That(model.ExcludedPackageBaseNames.Count, Is.EqualTo(StatusDashboardService.ExclusionRenderCap));
            Assert.That(model.ExcludedPackageBaseNames.First(), Is.EqualTo("base-000"));
        });
    }

    [Test]
    public async Task GetAsync_counts_seeded_packages_without_enumerating_names()
    {
        var service = CreateService(IndexWithPackages(Meta("one")), packageService: new CountingPackageService(7));

        var model = await service.GetAsync();

        Assert.That(model.SeededPackages, Is.EqualTo(7));
    }

    [Test]
    public async Task GetAsync_caches_the_assembled_model_for_repeated_reads()
    {
        var counting = new CountingPackageService(3);
        var service = CreateService(IndexWithPackages(Meta("one")), packageService: counting);

        var first = await service.GetAsync();
        var second = await service.GetAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ReferenceEquals(first, second), Is.True);
            Assert.That(counting.CountCalls, Is.EqualTo(1));
            Assert.That(second.AssembledUtc, Is.EqualTo(first.AssembledUtc));
        });
    }

    [Test]
    public void GetAsync_propagates_cancellation()
    {
        var service = CreateService(IndexWithPackages(Meta("one")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => service.GetAsync(cts.Token));
    }

    [Test]
    public void GetAsync_propagates_repository_failures()
    {
        var service = CreateService(IndexWithPackages(), packageService: new ThrowingPackageService());

        Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync());
    }

    private sealed class CountingPackageService(int seededCount) : IPackageService
    {
        public int CountCalls;

        public Task<IReadOnlyList<string>> ListAsync()
            => throw new NotSupportedException("the status dashboard must use CountAsync, not ListAsync");

        public Task<int> CountAsync()
        {
            CountCalls++;
            return Task.FromResult(seededCount);
        }

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();


        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

    }

    private sealed class ThrowingPackageService : IPackageService
    {
        public Task<IReadOnlyList<string>> ListAsync()
            => Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("boom"));

        public Task<int> CountAsync()
            => Task.FromException<int>(new InvalidOperationException("boom"));

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default) => throw new NotSupportedException();


        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
