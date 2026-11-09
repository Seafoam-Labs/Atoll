using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Atoll.Api.Extensions;
using Microsoft.Extensions.Options;

namespace Atoll.Api.Services.Catalog.Refresh;

/// <summary>
///     Transport/decompression/parsing boundary for the AUR metadata archive. Returns a
///     discriminated <see cref="AurMetadataResult" /> that distinguishes a 304 (archive
///     unchanged) from a validated, non-empty snapshot and carries the response validators
///     the caller retains for the next conditional request.
/// </summary>
public sealed class AurMetadataClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AtollOptions> options,
    ILogger<AurMetadataClient> logger)
{
    /// <summary>
    ///     The AUR is fronted by Anubis, which hard-rejects a missing or empty User-Agent with
    ///     an access-denied page, so the request must identify itself with a resolvable info URL.
    /// </summary>
    private const string UserAgent = "AtollBot/1.0 (+https://atoll.seafoam-labs.org/)";

    private static ReadOnlySpan<byte> GzipMagic => [0x1f, 0x8b];

    /// <summary>Leading body bytes used to identify the payload, and the size of the failure preview.</summary>
    private const int ProbeLength = 256;

    public async Task<AurMetadataResult> FetchAsync(
        EntityTagHeaderValue? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct)
    {
        logger.LogDebug("Fetching updated package data from AUR.");

        using var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.DataSource.DataFileUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(etag);
        if (lastModified is not null)
            request.Headers.IfModifiedSince = lastModified;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new AurMetadataResult.NotModified();

        response.EnsureSuccessStatusCode();

        // Confirm the body really starts with the gzip magic before the decompressor sees it. An
        // anti-bot gateway answers 200 with an HTML page, and GZipStream reports that only as an
        // unsupported compression method, far from the cause.
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var probe = new byte[ProbeLength];
        var probeLength = await body.ReadAtLeastAsync(probe, GzipMagic.Length, throwOnEndOfStream: false, ct);
        if (probeLength < GzipMagic.Length || !probe.AsSpan(0, GzipMagic.Length).SequenceEqual(GzipMagic))
            throw new InvalidDataException(NotGzipMessage(response, probe.AsSpan(0, probeLength)));

        // The probe bytes are part of the archive, so hand them back ahead of the unread body.
        await using var archive = new ProbedStream(body, probe.AsMemory(0, probeLength));
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);

        var packages = await ParsePackagesAsync(gzip, ct);
        if (packages.Count == 0)
            throw new InvalidDataException("AUR package dump contained no packages.");

        return new AurMetadataResult.Snapshot(
            packages,
            response.Headers.ETag,
            response.Content.Headers.LastModified);
    }

    private static string NotGzipMessage(HttpResponseMessage response, ReadOnlySpan<byte> body)
    {
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "<missing>";
        var preview = Encoding.UTF8.GetString(body)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();

        return $"AUR metadata dump response is not gzip (content-type: {contentType}, body starts with: \"{preview}\"). " +
               "Likely an anti-bot gateway interstitial: check the request User-Agent and the Anubis policy.";
    }

    private static async Task<IReadOnlyList<AurPackageMetadata>> ParsePackagesAsync(
        Stream gzipStream,
        CancellationToken ct)
    {
        // The whole decompressed dump is held in memory (~110k packages today).
        // If the dump grows significantly, switch to Utf8JsonReader / DeserializeAsyncEnumerable.
        using var doc = await JsonDocument.ParseAsync(gzipStream, cancellationToken: ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("AUR package dump is not a JSON array.");

        var packages = new List<AurPackageMetadata>();
        using var array = doc.RootElement.EnumerateArray();
        foreach (var element in array)
        {
            if (!element.TryGetProperty("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            packages.Add(element.DeserializeAurPackage());
        }

        return packages;
    }

    /// <summary>
    ///     Read-only, forward-only view that presents the bytes already read to identify the payload,
    ///     followed by the rest of the body, so sniffing stays invisible to the reader behind it.
    ///     The body stays owned by the response, which is what closes it.
    /// </summary>
    private sealed class ProbedStream(Stream body, ReadOnlyMemory<byte> probe) : Stream
    {
        private ReadOnlyMemory<byte> _probe = probe;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(Span<byte> buffer)
        {
            var read = CopyProbe(buffer);
            return read > 0 ? read : body.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = CopyProbe(buffer.Span);
            return read > 0 ? read : await body.ReadAsync(buffer, ct);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int CopyProbe(Span<byte> buffer)
        {
            if (_probe.IsEmpty || buffer.IsEmpty) return 0;

            var count = Math.Min(_probe.Length, buffer.Length);
            _probe.Span[..count].CopyTo(buffer[..count]);
            _probe = _probe[count..];
            return count;
        }
    }
}

public abstract record AurMetadataResult
{
    public sealed record NotModified : AurMetadataResult;

    public sealed record Snapshot(
        IReadOnlyList<AurPackageMetadata> Packages,
        EntityTagHeaderValue? ETag,
        DateTimeOffset? LastModified) : AurMetadataResult;
}
