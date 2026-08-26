using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atoll.Api.Services.Catalog.Refresh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class AurMetadataClientTests
{
    [Test]
    public async Task FetchAsync_returns_NotModified_on_304()
    {
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = Client(handler);

        var result = await client.FetchAsync(null, null, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<AurMetadataResult.NotModified>());
    }

    [Test]
    public async Task FetchAsync_returns_snapshot_with_packages_and_validators()
    {
        var etag = new EntityTagHeaderValue("\"v1\"");
        var lastModified = DateTimeOffset.UtcNow;
        var handler = new ControlHandler(_ =>
            Ok("[{\"ID\":1,\"Name\":\"demo\",\"PackageBase\":\"demo\",\"Version\":\"1.0-1\"}]", etag, lastModified));
        var client = Client(handler);

        var result = await client.FetchAsync(null, null, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<AurMetadataResult.Snapshot>());
        var snapshot = (AurMetadataResult.Snapshot)result;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Packages.Select(p => p.Name), Is.EquivalentTo(["demo"]));
            Assert.That(snapshot.ETag, Is.EqualTo(etag));
            Assert.That(snapshot.LastModified, Is.EqualTo(lastModified));
        });
    }

    [Test]
    public async Task FetchAsync_sends_conditional_headers_for_retained_validators()
    {
        var etag = new EntityTagHeaderValue("\"v1\"");
        var lastModified = DateTimeOffset.UtcNow;
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = Client(handler);

        await client.FetchAsync(etag, lastModified, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.Headers.IfNoneMatch.Select(t => t.Tag), Is.EquivalentTo([etag.Tag]));
            Assert.That(handler.LastRequest.Headers.IfModifiedSince, Is.EqualTo(lastModified));
        });
    }

    [Test]
    public async Task FetchAsync_rejects_malformed_non_array_dump()
    {
        var handler = new ControlHandler(_ => Ok("not-json", new EntityTagHeaderValue("\"v1\""), DateTimeOffset.UtcNow));
        var client = Client(handler);

        await Assert.ThatAsync(() => client.FetchAsync(null, null, CancellationToken.None), Throws.InstanceOf<JsonException>());
    }

    [Test]
    public async Task FetchAsync_rejects_well_formed_but_empty_dump()
    {
        var handler = new ControlHandler(_ => Ok("[]", new EntityTagHeaderValue("\"v1\""), DateTimeOffset.UtcNow));
        var client = Client(handler);

        Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(null, null, CancellationToken.None));
    }

    [Test]
    public async Task FetchAsync_honors_cancellation()
    {
        var handler = new CancellationHandler();
        var client = Client(handler);
        using var cts = new CancellationTokenSource(0);

        await Assert.ThatAsync(() => client.FetchAsync(null, null, cts.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    private static AurMetadataClient Client(HttpMessageHandler handler)
    {
        return new AurMetadataClient(
            new HandlerHttpClientFactory(handler),
            Options.Create(new AtollOptions
            {
                DataSource = new DataSourceOptions { DataFileUrl = "https://example.test/packages.json.gz" }
            }),
            NullLogger<AurMetadataClient>.Instance);
    }

    private static HttpResponseMessage Ok(string json, EntityTagHeaderValue etag, DateTimeOffset lastModified)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Gzip(json))
        };
        response.Headers.ETag = etag;
        response.Content.Headers.LastModified = lastModified;
        return response;
    }

    private static byte[] Gzip(string value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(value));
        }

        return output.ToArray();
    }

    private sealed class ControlHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(handler, false);
        }
    }
}