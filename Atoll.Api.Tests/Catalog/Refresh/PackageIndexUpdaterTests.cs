using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class PackageIndexUpdaterTests
{

    private static UpstreamPackageReconciler InertReconciler()
    {
        return new UpstreamPackageReconciler(
            new SeededNamesPackageService([]),
            Options.Create(new AtollOptions { DataSource = new DataSourceOptions() }),
            NullLogger<UpstreamPackageReconciler>.Instance);
    }

    [Fact]
    public async Task DownloadAndReloadAsync_UnparseablePayload_RecordsFailureAndLeavesIndexUntouched()
    {
        var invalidPayload = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var store = new PackageIndexStore();
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 10
            }
        });
        var coordinator = new PackageIndexUpdater(store,
            new InMemoryAurMetadataRepository(),
            new AurMetadataClient(new StubHttpClientFactory(invalidPayload), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            InertReconciler());

        var outcome = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.Equal(PackageIndexRefreshOutcome.Failed, outcome);
            Assert.Equal(1, status.Attempts);
            Assert.Equal(0, status.Successes);
            Assert.Equal(1, status.Failures);
            Assert.NotNull(status.LastStartedUtc);
            Assert.NotNull(status.LastFailedUtc);
            Assert.Empty(store.Current.ByNames);
        });
    }

    [Fact]
    public async Task DownloadAndReloadAsync_SecondCycle_SendsRetainedValidatorsAndReturnsNotModified()
    {
        var payload = Gzip("[{\"ID\":1,\"Name\":\"demo\",\"PackageBase\":\"demo\",\"Version\":\"1.0-1\"}]");
        var handler = new ConditionalStubHttpMessageHandler(payload);
        var store = new PackageIndexStore();
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 5
            }
        });
        var coordinator = new PackageIndexUpdater(
            store,
            new InMemoryAurMetadataRepository(),
            new AurMetadataClient(new HandlerHttpClientFactory(handler), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            InertReconciler());

        var first = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var second = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.Multiple(() =>
        {
            // The 304 cycle must not read as a swap: only a new generation warms the derived views.
            Assert.Equal(PackageIndexRefreshOutcome.Refreshed, first);
            Assert.Equal(PackageIndexRefreshOutcome.NotModified, second);
            Assert.True(handler.SawConditionalRequest);
            Assert.Equivalent(new[] { "demo" }, store.Current.ByNames.Keys, strict: true);
            Assert.Equal(2, status.Attempts);
            Assert.Equal(2, status.Successes);
            Assert.Equal(0, status.Failures);
        });
    }

    [Fact]
    public async Task DownloadAndReloadAsync_EmptyDump_FailsAndRetainsPreviousState()
    {
        var aurMetadata = new InMemoryAurMetadataRepository();
        await aurMetadata.SyncAsync(FullSync(Meta("demo")), CancellationToken.None);
        var store = new PackageIndexStore();
        store.Replace(PackageIndexBuilder.BuildFromPackages([Meta("demo")]));
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 10
            }
        });
        var coordinator = new PackageIndexUpdater(store,
            aurMetadata,
            new AurMetadataClient(new StubHttpClientFactory(Gzip("[]")), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            InertReconciler());

        var outcome = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();
        var retained = await aurMetadata.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equal(PackageIndexRefreshOutcome.Failed, outcome);
            Assert.Equal(1, status.Failures);
            Assert.Equivalent(new[] { "demo" }, store.Current.ByNames.Keys, strict: true);
            Assert.Equivalent(new[] { "demo" }, retained.Select(p => p.Name), strict: true);
        });
    }

    [Fact]
    public async Task DownloadAndReloadAsync_SuspiciousShrink_DefersPruningUntilConfirmed()
    {
        var handler = new ScriptedHttpMessageHandler(
            _ => Ok(Dump(10), "\"v1\""),
            _ => Ok(Dump(5), "\"v2\""),
            _ => NotModified(),
            _ => Ok(Dump(5), "\"v2\""));
        var packages = new DeletingPackageService(Enumerable.Range(1, 10).Select(i => $"p{i}"));
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 5,
                PruneDeletedPackages = true
            }
        });
        var reconciler = new UpstreamPackageReconciler(packages, options, NullLogger<UpstreamPackageReconciler>.Instance);
        var coordinator = new PackageIndexUpdater(
            new PackageIndexStore(),
            new InMemoryAurMetadataRepository(),
            new AurMetadataClient(new HandlerHttpClientFactory(handler), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            reconciler);

        Assert.Equal(PackageIndexRefreshOutcome.Refreshed,
            await coordinator.DownloadAndReloadAsync(CancellationToken.None));
        Assert.Empty(packages.Deleted);

        Assert.Equal(PackageIndexRefreshOutcome.Refreshed,
            await coordinator.DownloadAndReloadAsync(CancellationToken.None));
        Assert.Empty(packages.Deleted);

        // The archive re-answers the retained old validators with 304; that is not the
        // confirmation download, so pruning must stay deferred.
        Assert.Equal(PackageIndexRefreshOutcome.NotModified,
            await coordinator.DownloadAndReloadAsync(CancellationToken.None));
        Assert.Empty(packages.Deleted);
        Assert.Equal("\"v1\"", handler.SeenValidators[2]!.Tag);

        Assert.Equal(PackageIndexRefreshOutcome.Refreshed,
            await coordinator.DownloadAndReloadAsync(CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "p10", "p6", "p7", "p8", "p9" }, packages.Deleted, strict: true);
            Assert.Equal(4, coordinator.GetStatus().Successes);
        });
    }

    [Fact]
    public async Task DownloadAndReloadAsync_SequentialCycles_PersistOnlyChangedPackages()
    {
        var handler = new ScriptedHttpMessageHandler(
            _ => Ok(Dump(2), "\"v1\""),
            _ => Ok(Dump(3), "\"v2\""),
            _ => Ok(Dump(3), "\"v3\""));
        var aurMetadata = new RecordingAurMetadataRepository();
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 5
            }
        });
        var coordinator = new PackageIndexUpdater(
            new PackageIndexStore(),
            aurMetadata,
            new AurMetadataClient(new HandlerHttpClientFactory(handler), options, NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            InertReconciler());

        await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var retained = await aurMetadata.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            // An empty first cycle means a fresh host, so it writes the whole snapshot; the second
            // writes only the added package; the third sees an identical dump and writes nothing.
            Assert.Equal([2, 1, 0], aurMetadata.Deltas.Select(d => d.Upserts.Count));
            Assert.Equal([0, 0, 0], aurMetadata.Deltas.Select(d => d.Removals.Count));
            Assert.Equal([0, 2, 3], aurMetadata.Deltas.Select(d => d.Unchanged));
            Assert.Equal(["p1", "p2", "p3"], retained.Select(p => p.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        });
    }

    private static AurMetadataDelta FullSync(params AurPackageMetadata[] packages)
    {
        return AurMetadataDelta.Compute(
            new Dictionary<string, AurPackageMetadata>(StringComparer.Ordinal), packages);
    }

    private static AurPackageMetadata Meta(string name)
    {
        return new AurPackageMetadata(0, name, 0, name, "1.0", "d", null, 0, 0, null, null, null, 0, 0, "",
            [], [], [], [], [], [], [], []);
    }

    private static string Dump(int count)
    {
        var entries = Enumerable.Range(1, count)
            .Select(i => $"{{\"ID\":{i},\"Name\":\"p{i}\",\"PackageBase\":\"p{i}\",\"Version\":\"1.0-1\"}}");
        return $"[{string.Join(',', entries)}]";
    }

    private static HttpResponseMessage Ok(string json, string etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Gzip(json))
        };
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        return response;
    }

    private static HttpResponseMessage NotModified()
    {
        return new HttpResponseMessage(HttpStatusCode.NotModified);
    }

    private static byte[] Gzip(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(value));
        return output.ToArray();
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingAurMetadataRepository : IAurMetadataRepository
    {
        private readonly InMemoryAurMetadataRepository _inner = new();

        public List<AurMetadataDelta> Deltas { get; } = [];

        public Task SyncAsync(AurMetadataDelta delta, CancellationToken ct)
        {
            Deltas.Add(delta);
            return _inner.SyncAsync(delta, ct);
        }

        public Task<IReadOnlyList<AurPackageMetadata>> LoadAsync(CancellationToken ct) => _inner.LoadAsync(ct);

        public Task<long> CountAsync(CancellationToken ct) => _inner.CountAsync(ct);

        public Task DeleteAsync(CancellationToken ct) => _inner.DeleteAsync(ct);
    }

    private sealed class ConditionalStubHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        private int _requests;

        public bool SawConditionalRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests++;
            if (_requests > 1)
            {
                SawConditionalRequest = request.Headers.IfNoneMatch.Any(tag => string.Equals(tag.Tag, "\"dump-1\"", StringComparison.Ordinal))
                                        && request.Headers.IfModifiedSince is not null;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"dump-1\"");
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }
    }

    private sealed class StubHttpClientFactory(byte[] payload) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(payload), disposeHandler: true);
    }

    private sealed class StubHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };

            return Task.FromResult(response);
        }
    }

    private sealed class ScriptedHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private readonly List<EntityTagHeaderValue?> _seenValidators = [];
        private int _requests;

        public IReadOnlyList<EntityTagHeaderValue?> SeenValidators => _seenValidators;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _seenValidators.Add(request.Headers.IfNoneMatch.FirstOrDefault());
            return Task.FromResult(responses[_requests++](request));
        }
    }

    private sealed class DeletingPackageService(IEnumerable<string> seededNames) : IPackageService
    {
        private readonly HashSet<string> _seeded = new(seededNames, StringComparer.Ordinal);

        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<string>> ListAsync()
        {
            IReadOnlyList<string> names = [.. _seeded];
            return Task.FromResult(names);
        }

        public Task<int> CountAsync()
        {
            return Task.FromResult(_seeded.Count);
        }

        public Task<PackageIndexResponse> GetIndexPageAsync(
            int page,
            int limit,
            PackageIndexSortBy sortBy = PackageIndexSortBy.Name,
            PackageIndexSortOrder? order = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task PrewarmAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(string packageName, CancellationToken ct = default)
        {
            _seeded.Remove(packageName);
            Deleted.Add(packageName);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PackageFiles> GetAsync(string packageName, string? commitSha = null)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PackageVersion>> GetHistoryAsync(string packageName)
            => throw new NotSupportedException();

        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

    }
}