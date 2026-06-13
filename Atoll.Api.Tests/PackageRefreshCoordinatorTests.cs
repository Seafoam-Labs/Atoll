using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests;

public class PackageRefreshCoordinatorTests
{
    [Test]
    public async Task RefreshCoordinatorTracksAttemptAndFailureMetrics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"atoll-refresh-{Guid.NewGuid():N}.json");
        var invalidPayload = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var store = new PackageIndexStore();
        var coordinator = new PackageRefreshCoordinator(store,
            new StubHttpClientFactory(invalidPayload),
            Options.Create(new AtollOptions
            {
                DataFile = path,
                DataFileUrl = "https://example.test/packages.json.gz",
                RefreshIntervalHours = 1
            }),
            NullLogger<PackageRefreshCoordinator>.Instance);

        var ok = await coordinator.DownloadAndReloadAsync(CancellationToken.None);
        var status = coordinator.GetStatus();

        Assert.That(ok, Is.False);
        Assert.That(status.Attempts, Is.EqualTo(1));
        Assert.That(status.Successes, Is.EqualTo(0));
        Assert.That(status.Failures, Is.EqualTo(1));
        Assert.That(status.LastStartedUtc, Is.Not.Null);
        Assert.That(status.LastFailedUtc, Is.Not.Null);
        Assert.That(store.Current.ByNames, Is.Empty);
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
}