using System.Net;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public class ApiVersioningEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public ApiVersioningEndpointsTests()
    {
        _factory = new ApiTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task V1RestSurfaceIsServedAndAdvertisesSupportedVersions()
    {
        var response = await _client.GetAsync("/v1/search?query=portable-kit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.Headers.TryGetValues("api-supported-versions", out var versions) &&
            versions.SequenceEqual(["1.0"]),
            "Expected api-supported-versions: 1.0");
    }

    [Fact]
    public async Task UnversionedRestRoutesReturn404()
    {
        var search = await _client.GetAsync("/search?query=portable-kit");
        var packages = await _client.GetAsync("/packages");
        var package = await _client.GetAsync("/packages/portable-kit");

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, packages.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, package.StatusCode);
        });
    }

    [Fact]
    public async Task UnsupportedUrlSegmentVersionReturns404()
    {
        var search = await _client.GetAsync("/v2/search?query=portable-kit");
        var packages = await _client.GetAsync("/v2/packages");

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, packages.StatusCode);
        });
    }

    [Fact]
    public async Task QueryStringVersioningIsNotHonored()
    {
        var response = await _client.GetAsync("/packages?api-version=1.0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ProtocolFixedSurfacesRemainVersionNeutral()
    {
        var health = await _client.GetAsync("/health");
        var rpc = await _client.GetAsync("/rpc?v=5&type=suggest&arg=portable");

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, rpc.StatusCode);
        });
    }

    [Fact]
    public async Task OpenApiDocumentIsServedPerVersionWithSubstitutedPaths()
    {
        var v1 = await _client.GetAsync("/openapi/v1.json");
        var bare = await _client.GetAsync("/openapi/1.0.json");

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        });

        var json = await v1.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        Assert.Multiple(() =>
        {
            Assert.True(paths.TryGetProperty("/v1/packages", out _));
            Assert.False(paths.TryGetProperty("/packages", out _));
            Assert.True(paths.TryGetProperty("/rpc", out _));
        });
    }
}
