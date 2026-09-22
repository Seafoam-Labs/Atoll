using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public sealed class MinimalApiEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public MinimalApiEndpointsTests()
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
    public async Task HealthGetAndHeadReturnOk()
    {
        var get = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task PackagesSupportsNameProvidesAndWordsQueries()
    {
        var byName = await _client.GetAsync("/v1/search?query=portable-kit,not-real", TestContext.Current.CancellationToken);
        var byProv = await _client.GetAsync("/v1/search?query=shelly&by=provides", TestContext.Current.CancellationToken);
        var byDesc = await _client.GetAsync("/v1/search?query=handheld,portable&by=words", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, byName.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byProv.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byDesc.StatusCode);

        var byNameBody = await byName.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var byProvBody = await byProv.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var byDescBody = await byDesc.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var byNameDoc = JsonDocument.Parse(byNameBody);
        using var byProvidesDoc = JsonDocument.Parse(byProvBody);
        using var byWordsDoc = JsonDocument.Parse(byDescBody);

        Assert.Equal(1, byNameDoc.RootElement.GetArrayLength());
        Assert.Equal("portable-kit", byNameDoc.RootElement[0].GetProperty("name").GetString());

        Assert.Equal(1, byProvidesDoc.RootElement.GetArrayLength());
        Assert.Equal("shelly-bin", byProvidesDoc.RootElement[0].GetProperty("name").GetString());

        Assert.Equal(2, byWordsDoc.RootElement.GetArrayLength());
        Assert.Equal("portable-pro", byWordsDoc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("portable-kit", byWordsDoc.RootElement[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task InvalidPackagesByAndUnknownRouteReturnTextHtml404()
    {
        var invalidBy = await _client.GetAsync("/v1/search?query=shelly&by=unknown", TestContext.Current.CancellationToken);
        var unknown = await _client.GetAsync("/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalidBy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task MetricsReturnsPrometheusText()
    {
        _ = await _client.GetAsync("/v1/search?query=portable-kit", TestContext.Current.CancellationToken);

        var response = await _client.GetAsync("/metrics", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

        Assert.Multiple(() =>
        {
            // The search request above is counted, and the sample index has 3 names / 3 provides.
            Assert.Matches(@"atoll_search_requests_total\{[^}]*\} [1-9]", body);
            Assert.Matches("""atoll_index_size\{[^}]*index="names"[^}]*\} 3""", body);
            Assert.Matches("""atoll_index_size\{[^}]*index="provides"[^}]*\} 3""", body);
            Assert.Matches("""atoll_index_size\{[^}]*index="words"[^}]*\} [1-9]""", body);

            // Uptime gauge plus ASP.NET Core request metrics from instrumentation.
            Assert.Contains("atoll_process_uptime_seconds", body, StringComparison.Ordinal);
            Assert.Contains("http_server_request_duration_seconds", body, StringComparison.Ordinal);
        });
    }
}