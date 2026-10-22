using System.Net;
using System.Text.Json;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

/// <summary>
///     Pins the additive REST content shape: valid UTF-8 stays in <c>files</c>, invalid UTF-8 moves to
///     <c>binaryFiles</c> as base64, and the binary map is omitted entirely for text-only packages.
/// </summary>
public sealed class PackageContentEndpointsTests : IDisposable
{
    private static readonly byte[] PngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0xFF
    ];

    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public PackageContentEndpointsTests()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetPackage_TextOnly_OmitsBinaryFiles()
    {
        await SeedAsync(("PKGBUILD", "pkgname=test\n"u8.ToArray()));

        using var body = await GetJsonAsync("/v1/packages/pkg");

        var files = body.RootElement.GetProperty("files");
        Assert.Multiple(() =>
        {
            Assert.Equal("pkgname=test\n", files.GetProperty("PKGBUILD").GetString());
            Assert.False(body.RootElement.TryGetProperty("binaryFiles", out _));
        });
    }

    [Fact]
    public async Task GetPackage_BinaryFile_MovesToBinaryFilesAsBase64()
    {
        await SeedAsync(
            ("PKGBUILD", "pkgname=test\n"u8.ToArray()),
            ("icon64.png", PngBytes));

        using var body = await GetJsonAsync("/v1/packages/pkg");

        var files = body.RootElement.GetProperty("files");
        Assert.Multiple(() =>
        {
            Assert.Equal("pkgname=test\n", files.GetProperty("PKGBUILD").GetString());
            Assert.False(files.TryGetProperty("icon64.png", out _));

            var base64 = body.RootElement.GetProperty("binaryFiles").GetProperty("icon64.png").GetString();
            Assert.Equal(PngBytes, Convert.FromBase64String(base64!));
        });
    }

    [Fact]
    public async Task GetPackageVersion_BinaryFile_UsesSameSplit()
    {
        await SeedAsync(
            ("PKGBUILD", "pkgname=test\n"u8.ToArray()),
            ("icon64.png", PngBytes));

        using var body = await GetJsonAsync("/v1/packages/pkg/versions/rev-1");

        var files = body.RootElement.GetProperty("files");
        Assert.Multiple(() =>
        {
            Assert.False(files.TryGetProperty("icon64.png", out _));
            var base64 = body.RootElement.GetProperty("binaryFiles").GetProperty("icon64.png").GetString();
            Assert.Equal(PngBytes, Convert.FromBase64String(base64!));
        });
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await _client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument.Parse(json);
    }

    private async Task SeedAsync(params (string Name, byte[] Bytes)[] files)
    {
        var now = DateTimeOffset.UtcNow;
        await _factory.Repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = "pkg",
                PackageName = "pkg",
                CreatedAt = now,
                UpdatedAt = now,
                HeadRevisionId = "rev-1",
                Revisions =
                [
                    new PackageRevisionDocument
                    {
                        RevisionId = "rev-1",
                        CreatedAt = now,
                        Author = "test",
                        Message = "seed"
                    }
                ]
            },
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId("pkg", "rev-1"),
                PackageName = "pkg",
                RevisionId = "rev-1",
                CreatedAt = now,
                Author = "test",
                Message = "seed",
                Files = files.ToDictionary(
                    file => file.Name,
                    file => PackageFileFactory.FromBytes(file.Bytes),
                    StringComparer.Ordinal)
            },
            TestContext.Current.CancellationToken);
        await _factory.SecurityRepository.ScanRevisionAsync("pkg", "rev-1", SecurityStatus.Verified);
    }
}
