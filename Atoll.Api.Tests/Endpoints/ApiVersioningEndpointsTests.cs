using System.Net;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public sealed class ApiVersioningEndpointsTests : IDisposable
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
    public async Task GetV1Search_ServedWithApiSupportedVersionsHeader()
    {
        var response = await _client.GetAsync("/v1/search?query=portable-kit", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            response.Headers.TryGetValues("api-supported-versions", out var versions) &&
            versions.SequenceEqual(["1.0"], StringComparer.Ordinal),
            "Expected api-supported-versions: 1.0");
    }

    [Fact]
    public async Task MapEndpoints_UnversionedRestRoutes_Return404()
    {
        var search = await _client.GetAsync("/search?query=portable-kit", TestContext.Current.CancellationToken);
        var packages = await _client.GetAsync("/packages", TestContext.Current.CancellationToken);
        var package = await _client.GetAsync("/packages/portable-kit", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, packages.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, package.StatusCode);
        });
    }

    [Fact]
    public async Task MapEndpoints_UnsupportedUrlSegmentVersion_Returns404()
    {
        var search = await _client.GetAsync("/v2/search?query=portable-kit", TestContext.Current.CancellationToken);
        var packages = await _client.GetAsync("/v2/packages", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.NotFound, search.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, packages.StatusCode);
        });
    }

    [Fact]
    public async Task MapEndpoints_QueryStringVersioning_IsNotHonored()
    {
        var response = await _client.GetAsync("/packages?api-version=1.0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MapEndpoints_ProtocolFixedSurfaces_RemainVersionNeutral()
    {
        var health = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        var rpc = await _client.GetAsync("/rpc?v=5&type=suggest&arg=portable", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal(HttpStatusCode.OK, rpc.StatusCode);
        });
    }

    [Fact]
    public async Task GetOpenApiV1Json_ServedPerVersionWithSubstitutedPaths()
    {
        var v1 = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var bare = await _client.GetAsync("/openapi/1.0.json", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        });

        var json = await v1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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
