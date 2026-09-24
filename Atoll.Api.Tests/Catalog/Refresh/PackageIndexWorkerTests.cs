using System.Net;
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
            CreateUpdater(store), catalog, packages, NullLogger<PackageIndexWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await WaitForPrewarmAsync(packages);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            // The first list builds the catalog snapshot, the prewarm call is the ranker name list.
            Assert.Equal(1, packages.ListCalls);
            Assert.Equal(1, packages.PrewarmCalls);
        });
    }

    private static PackageIndexUpdater CreateUpdater(PackageIndexStore store)
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
            new AurMetadataClient(new StubHttpClientFactory([0x01, 0x02, 0x03, 0x04]), options,
                NullLogger<AurMetadataClient>.Instance),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            new UpstreamPackageReconciler(new SeededNamesPackageService([]), options,
                NullLogger<UpstreamPackageReconciler>.Instance));
    }

    private static async Task WaitForPrewarmAsync(SeededNamesPackageService packages, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (packages.PrewarmCalls > 0) return;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The worker did not prewarm the caches within {timeoutMs} ms.");
        throw new InvalidOperationException("Unreachable after assertion failure.");
    }

    // The worker's first loop iteration fetches the metadata dump, so the updater needs an HTTP stub;
    // an invalid payload makes that attempt fail harmlessly while the prewarm stays in focus.
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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        }
    }
}