using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Search.Refresh;

public class PackageIndexUpdaterTests
{
    [Test]
    public async Task RefreshCoordinatorTracksAttemptAndFailureMetrics()
    {
        var invalidPayload = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var store = new PackageIndexStore();
        var coordinator = new PackageIndexUpdater(store,
            new InMemoryAurMetadataRepository(),
            new StubHttpClientFactory(invalidPayload),
            Options.Create(new AtollOptions
            {
                DataSource = new DataSourceOptions
                {
                    DataFileUrl = "https://example.test/packages.json.gz",
                    RefreshIntervalMinutes = 10
                }
            }),
            NullLogger<PackageIndexUpdater>.Instance);

        var ok = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(status.Attempts, Is.EqualTo(1));
            Assert.That(status.Successes, Is.EqualTo(0));
            Assert.That(status.Failures, Is.EqualTo(1));
            Assert.That(status.LastStartedUtc, Is.Not.Null);
            Assert.That(status.LastFailedUtc, Is.Not.Null);
            Assert.That(store.Current.ByNames, Is.Empty);
        });
    }

    [Test]
    public async Task DownloadAndReloadAsync_uses_archive_validators_after_successful_download()
    {
        var payload = Gzip("[{\"ID\":1,\"Name\":\"demo\",\"PackageBase\":\"demo\",\"Version\":\"1.0-1\"}]");
        var handler = new ConditionalStubHttpMessageHandler(payload);
        var store = new PackageIndexStore();
        var coordinator = new PackageIndexUpdater(
            store,
            new InMemoryAurMetadataRepository(),
            new HandlerHttpClientFactory(handler),
            Options.Create(new AtollOptions
            {
                DataSource = new DataSourceOptions
                {
                    DataFileUrl = "https://example.test/packages.json.gz",
                    RefreshIntervalMinutes = 5
                }
            }),
            NullLogger<PackageIndexUpdater>.Instance);

        var first = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var second = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.True);
            Assert.That(handler.SawConditionalRequest, Is.True);
            Assert.That(store.Current.ByNames.Keys, Is.EquivalentTo(["demo"]));
            Assert.That(status.Attempts, Is.EqualTo(2));
            Assert.That(status.Successes, Is.EqualTo(2));
            Assert.That(status.Failures, Is.Zero);
        });
    }

    [Test]
    public async Task DownloadAndReloadAsync_rejects_well_formed_but_empty_dump()
    {
        var aurMetadata = new InMemoryAurMetadataRepository();
        await aurMetadata.SaveAsync([Meta("demo")], CancellationToken.None);
        var store = new PackageIndexStore();
        store.Replace(PackageDataLoader.BuildFromPackages([Meta("demo")]));
        var coordinator = new PackageIndexUpdater(store,
            aurMetadata,
            new StubHttpClientFactory(Gzip("[]")),
            Options.Create(new AtollOptions
            {
                DataSource = new DataSourceOptions
                {
                    DataFileUrl = "https://example.test/packages.json.gz",
                    RefreshIntervalMinutes = 10
                }
            }),
            NullLogger<PackageIndexUpdater>.Instance);

        var ok = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();
        var retained = await aurMetadata.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(status.Failures, Is.EqualTo(1));
            Assert.That(store.Current.ByNames.Keys, Is.EquivalentTo(["demo"]));
            Assert.That(retained.Select(p => p.Name), Is.EquivalentTo(["demo"]));
        });
    }

    [Test]
    public async Task DownloadAndReloadAsync_defers_pruning_until_a_suspicious_shrink_is_confirmed()
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
            new HandlerHttpClientFactory(handler),
            options,
            NullLogger<PackageIndexUpdater>.Instance,
            reconciler);

        Assert.That(await coordinator.DownloadAndReloadAsync(CancellationToken.None), Is.True);
        Assert.That(packages.Deleted, Is.Empty, "full snapshot deletes nothing");

        Assert.That(await coordinator.DownloadAndReloadAsync(CancellationToken.None), Is.True);
        Assert.That(packages.Deleted, Is.Empty, "a sudden >10% shrink defers pruning for one cycle");

        // The archive re-answers the retained old validators with 304; that is not the
        // confirmation download, so pruning must stay deferred.
        Assert.That(await coordinator.DownloadAndReloadAsync(CancellationToken.None), Is.True);
        Assert.That(packages.Deleted, Is.Empty, "a 304 while confirmation is pending prunes nothing");
        Assert.That(handler.SeenValidators[2]!.Tag, Is.EqualTo("\"v1\""), "the old validators were retained");

        Assert.That(await coordinator.DownloadAndReloadAsync(CancellationToken.None), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(packages.Deleted, Is.EquivalentTo(["p10", "p6", "p7", "p8", "p9"]),
                "the confirmation download prunes the packages absent upstream");
            Assert.That(coordinator.GetStatus().Successes, Is.EqualTo(4));
        });
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
        return $"[{string.Join(",", entries)}]";
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
                SawConditionalRequest = request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"dump-1\"")
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
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHttpMessageHandler(payload), true);
        }
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

        public Task SyncFromStorageAsync(string packageName)
            => throw new NotSupportedException();

        public Task SyncToStorageAsync(string packageName)
            => throw new NotSupportedException();

        public Task SeedFromAurAsync(string packageName)
            => throw new NotSupportedException();

        public Task SeedFilesAsync(string packageName, IReadOnlyDictionary<string, string> files)
            => throw new NotSupportedException();

        public Task<bool> AppendRevisionFromUpstreamAsync(
            string packageName, IReadOnlyDictionary<string, string> files, CancellationToken ct = default)
            => throw new NotSupportedException();

        public string? GetRepositoryPath(string packageName)
            => throw new NotSupportedException();

        public Task EnsureGitRepositoryAsync(string packageName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}