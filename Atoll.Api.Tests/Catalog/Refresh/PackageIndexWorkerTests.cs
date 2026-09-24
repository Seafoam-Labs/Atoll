using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Ui;
using Atoll.Api.Tests.Fakes;
using Atoll.Api.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class PackageIndexWorkerTests
{
    // A payload the metadata client cannot parse, so the loop's refresh attempt fails harmlessly
    // while the prewarm stays in focus.
    private static readonly byte[] InvalidPayload = [0x01, 0x02, 0x03, 0x04];

    [Fact]
    public async Task Startup_prewarms_the_catalog_snapshot_and_the_ranker_names()
    {
        var packages = new SeededNamesPackageService([]);
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var catalog = new PackageCatalogService(
            store, packages, new InMemoryPackageSecurityRepository(), TestHybridCache.New(),
            Options.Create(new AtollOptions()));

        var worker = new PackageIndexWorker(
            CreateUpdater(store, new StubHttpMessageHandler(InvalidPayload)),
            catalog, packages, NullLogger<PackageIndexWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitForPrewarmAsync(packages, expectedCalls: 1);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // The first list builds the catalog snapshot, the prewarm call is the ranker name list.
            Assert.Equal(1, packages.ListCalls);
            Assert.Equal(1, packages.PrewarmCalls);
        });
    }

    [Fact]
    public async Task A_refresh_swap_warms_the_caches_again_without_rescanning_the_snapshot()
    {
        var packages = new SeededNamesPackageService([]);
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var catalog = new PackageCatalogService(
            store, packages, new InMemoryPackageSecurityRepository(), TestHybridCache.New(),
            Options.Create(new AtollOptions()));

        var worker = new PackageIndexWorker(
            CreateUpdater(store, new StubHttpMessageHandler(
                Gzip("""[{"ID":1,"Name":"demo","PackageBase":"demo","Version":"1.0-1"}]"""))),
            catalog, packages, NullLogger<PackageIndexWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitForPrewarmAsync(packages, expectedCalls: 2);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // The second warm follows the swap; the snapshot survives it as a cache hit.
            Assert.Equal(1, packages.ListCalls);
            Assert.Equal(2, packages.PrewarmCalls);
        });
    }

    [Fact]
    public async Task A_cycle_that_reached_the_archive_warms_and_a_failed_one_does_not()
    {
        var packages = new SeededNamesPackageService([]);
        var store = new PackageIndexStore();
        store.Replace(TestData.LoadSampleIndexes());
        var catalog = new PackageCatalogService(
            store, packages, new InMemoryPackageSecurityRepository(), TestHybridCache.New(),
            Options.Create(new AtollOptions()));

        // The worker never starts, so the cycles run one at a time: a swap, a 304, then a fetch that
        // fails to parse. The 304 warms as well, because a warm that finds a live entry re-arms its
        // TTL and a 304 cycle can still have pruned packages.
        var worker = new PackageIndexWorker(
            CreateUpdater(store, new ScriptedHttpMessageHandler(
                Ok(Gzip("""[{"ID":1,"Name":"demo","PackageBase":"demo","Version":"1.0-1"}]"""), "\"dump-1\""),
                new HttpResponseMessage(HttpStatusCode.NotModified),
                Ok(InvalidPayload))),
            catalog, packages, NullLogger<PackageIndexWorker>.Instance);

        var ct = TestContext.Current.CancellationToken;
        var outcomes = new[]
        {
            await worker.RefreshAndWarmAsync(ct),
            await worker.RefreshAndWarmAsync(ct),
            await worker.RefreshAndWarmAsync(ct)
        };

        Assert.Multiple(() =>
        {
            Assert.Equal(
                [
                    PackageIndexRefreshOutcome.Refreshed,
                    PackageIndexRefreshOutcome.NotModified,
                    PackageIndexRefreshOutcome.Failed
                ],
                outcomes);
            Assert.Equal(2, packages.PrewarmCalls);

            // The unchanged cycle re-reads the snapshot from the cache instead of rescanning for it.
            Assert.Equal(1, packages.ListCalls);
        });
    }

    private static PackageIndexUpdater CreateUpdater(PackageIndexStore store, HttpMessageHandler handler)
    {
        var options = Options.Create(new AtollOptions
        {
            DataSource = new DataSourceOptions
            {
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalMinutes = 10
            }
        });

        return new PackageIndexUpdater(
            store,
            new InMemoryAurMetadataRepository(),
            new AurMetadataClient(new SharedHandlerHttpClientFactory(handler), options,
                NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            new UpstreamPackageReconciler(new SeededNamesPackageService([]), options,
                NullLogger<UpstreamPackageReconciler>.Instance));
    }

    private static HttpResponseMessage Ok(byte[] payload, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        };

        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
            response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
        }

        return response;
    }

    private static byte[] Gzip(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(value));
        return output.ToArray();
    }

    private static async Task WaitForPrewarmAsync(
        SeededNamesPackageService packages, int expectedCalls, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (packages.PrewarmCalls >= expectedCalls) return;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The worker did not prewarm the caches {expectedCalls} time(s) within {timeoutMs} ms.");
        throw new InvalidOperationException("Unreachable after assertion failure.");
    }

    // The worker's first loop iteration fetches the metadata dump, so the updater needs an HTTP stub.
    // One handler is shared across the clients the updater creates, one per cycle, so it can be
    // scripted per request.
    private sealed class SharedHandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(Ok(payload));
    }

    private sealed class ScriptedHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responses[_requests++]);
    }
}