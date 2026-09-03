using System.Net;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class ApiVersioningEndpointsTests
{
    private HttpClient _client = null!;
    private ApiTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new ApiTestFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task V1RestSurfaceIsServedAndAdvertisesSupportedVersions()
    {
        var response = await _client.GetAsync("/v1/search?query=portable-kit");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(
            response.Headers.TryGetValues("api-supported-versions", out var versions) &&
            versions.SequenceEqual(["1.0"]),
            Is.True,
            "Expected api-supported-versions: 1.0");
    }

    [Test]
    public async Task UnversionedRestRoutesReturn404()
    {
        var search = await _client.GetAsync("/search?query=portable-kit");
        var packages = await _client.GetAsync("/packages");
        var package = await _client.GetAsync("/packages/portable-kit");

        Assert.Multiple(() =>
        {
            Assert.That(search.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(packages.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(package.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });
    }

    [Test]
    public async Task UnsupportedUrlSegmentVersionReturns404()
    {
        var response = await _client.GetAsync("/v2/search?query=portable-kit");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task QueryStringVersioningIsNotHonored()
    {
        var response = await _client.GetAsync("/packages?api-version=1.0");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ProtocolFixedSurfacesRemainVersionNeutral()
    {
        var health = await _client.GetAsync("/health");
        var rpc = await _client.GetAsync("/rpc?v=5&type=suggest&arg=portable");

        Assert.Multiple(() =>
        {
            Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(rpc.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        });
    }

    [Test]
    public async Task OpenApiDocumentIsServedPerVersionWithSubstitutedPaths()
    {
        var v1 = await _client.GetAsync("/openapi/v1.json");
        var bare = await _client.GetAsync("/openapi/1.0.json");

        Assert.Multiple(() =>
        {
            Assert.That(v1.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(bare.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        });

        var json = await v1.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var paths = doc.RootElement.GetProperty("paths");

        Assert.Multiple(() =>
        {
            Assert.That(paths.TryGetProperty("/v1/packages", out _), Is.True);
            Assert.That(paths.TryGetProperty("/packages", out _), Is.False);
            Assert.That(paths.TryGetProperty("/rpc", out _), Is.True);
        });
    }
}
