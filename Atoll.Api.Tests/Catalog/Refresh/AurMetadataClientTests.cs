using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atoll.Api.Services.Catalog.Refresh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Refresh;

public class AurMetadataClientTests
{
    [Fact]
    public async Task FetchAsync_NotModifiedResponse_ReturnsNotModifiedResult()
    {
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = Client(handler);

        var result = await client.FetchAsync(null, null, CancellationToken.None);

        Assert.IsType<AurMetadataResult.NotModified>(result, exactMatch: false);
    }

    [Fact]
    public async Task FetchAsync_OkResponse_ReturnsSnapshotWithPackagesAndValidators()
    {
        var etag = new EntityTagHeaderValue("\"v1\"");
        var lastModified = DateTimeOffset.UtcNow;
        var handler = new ControlHandler(_ =>
            Ok("[{\"ID\":1,\"Name\":\"demo\",\"PackageBase\":\"demo\",\"Version\":\"1.0-1\"}]", etag, lastModified));
        var client = Client(handler);

        var result = await client.FetchAsync(null, null, CancellationToken.None);

        var snapshot = Assert.IsType<AurMetadataResult.Snapshot>(result, exactMatch: false);
        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { "demo" }, snapshot.Packages.Select(p => p.Name), strict: true);
            Assert.Equal(etag, snapshot.ETag);
            Assert.Equal(lastModified, snapshot.LastModified);
        });
    }

    [Fact]
    public async Task FetchAsync_RetainedValidators_SendsConditionalHeaders()
    {
        var etag = new EntityTagHeaderValue("\"v1\"");
        var lastModified = DateTimeOffset.UtcNow;
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = Client(handler);

        await client.FetchAsync(etag, lastModified, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.Equivalent(new[] { etag.Tag }, handler.LastRequest!.Headers.IfNoneMatch.Select(t => t.Tag), strict: true);
            Assert.Equal(lastModified, handler.LastRequest.Headers.IfModifiedSince);
        });
    }

    [Fact]
    public async Task FetchAsync_SendsIdentifyingUserAgent()
    {
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var client = Client(handler);

        await client.FetchAsync(null, null, CancellationToken.None);

        // Anubis rejects an empty User-Agent with an access-denied page, so the bot must name itself.
        Assert.Equal("AtollBot/1.0 (+https://atoll.seafoam-labs.org/)",
            handler.LastRequest!.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task FetchAsync_AntiBotHtmlBody_ThrowsDescriptiveInvalidDataException()
    {
        var body = "<html>\n<head><title>Access Denied</title></head>\n<body>Access Denied</body>\n</html>";
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html")
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.FetchAsync(null, null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.Contains("not gzip", exception.Message, StringComparison.Ordinal);
            Assert.Contains("text/html", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Access Denied", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task FetchAsync_EmptyBody_ThrowsDescriptiveInvalidDataException()
    {
        var handler = new ControlHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        });
        var client = Client(handler);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.FetchAsync(null, null, CancellationToken.None));

        Assert.Contains("not gzip", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)] // a dump outgrowing the sniff probe, so bytes keep arriving after it
    public async Task FetchAsync_OneByteAtATimeTransport_ParsesSnapshot(int packageCount)
    {
        // Sniffing must hand the probe bytes back to the decompressor, however the transport
        // splits the body into reads.
        var etag = new EntityTagHeaderValue("\"v1\"");
        var lastModified = DateTimeOffset.UtcNow;
        var handler = new ControlHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new OneByteAtATimeStream(Gzip(Dump(packageCount))))
            };
            response.Headers.ETag = etag;
            response.Content.Headers.LastModified = lastModified;
            return response;
        });
        var client = Client(handler);

        var result = await client.FetchAsync(null, null, CancellationToken.None);

        var snapshot = Assert.IsType<AurMetadataResult.Snapshot>(result, exactMatch: false);
        Assert.Equal($"p{packageCount}", snapshot.Packages[^1].Name);
        Assert.Equal(etag, snapshot.ETag);
    }

    [Fact]
    public async Task FetchAsync_MalformedNonArrayDump_ThrowsJsonException()
    {
        var handler = new ControlHandler(_ => Ok("not-json", new EntityTagHeaderValue("\"v1\""), DateTimeOffset.UtcNow));
        var client = Client(handler);

        await Assert.ThrowsAnyAsync<JsonException>(() => client.FetchAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_EmptyDump_ThrowsInvalidDataException()
    {
        var handler = new ControlHandler(_ => Ok("[]", new EntityTagHeaderValue("\"v1\""), DateTimeOffset.UtcNow));
        var client = Client(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.FetchAsync(null, null, CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var handler = new CancellationHandler();
        var client = Client(handler);
        using var cts = new CancellationTokenSource(0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchAsync(null, null, cts.Token));
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

    private static string Dump(int count)
    {
        var entries = Enumerable.Range(1, count)
            .Select(i => $"{{\"ID\":{i},\"Name\":\"p{i}\",\"PackageBase\":\"p{i}\",\"Version\":\"1.0-1\"}}");
        return $"[{string.Join(',', entries)}]";
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

    /// <summary>Handy stand-in for a socket stream: every read delivers at most one byte.</summary>
    private sealed class OneByteAtATimeStream(byte[] payload) : MemoryStream(payload)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => base.ReadAsync(buffer, offset, Math.Min(count, 1), ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], ct);
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
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}