using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
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
    public async Task<AurMetadataResult> FetchAsync(
        EntityTagHeaderValue? etag,
        DateTimeOffset? lastModified,
        CancellationToken ct)
    {
        logger.LogDebug("Fetching updated package data from AUR.");

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, options.Value.DataSource.DataFileUrl);
        if (etag is not null)
            request.Headers.IfNoneMatch.Add(etag);
        if (lastModified is not null)
            request.Headers.IfModifiedSince = lastModified;

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
            return new AurMetadataResult.NotModified();

        response.EnsureSuccessStatusCode();
        await using var compressed = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);

        var packages = await ParsePackagesAsync(gzip, ct);
        if (packages.Count == 0)
            throw new InvalidDataException("AUR package dump contained no packages.");

        return new AurMetadataResult.Snapshot(
            packages,
            response.Headers.ETag,
            response.Content.Headers.LastModified);
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
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("Name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String) continue;

            var name = nameElement.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            packages.Add(element.DeserializeAurPackage());
        }

        return packages;
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