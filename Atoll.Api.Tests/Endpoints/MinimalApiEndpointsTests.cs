using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class MinimalApiEndpointsTests
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
    public async Task HealthGetAndHeadReturnOk()
    {
        var get = await _client.GetAsync("/health");
        var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));

        Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task PackagesSupportsNameProvidesAndWordsQueries()
    {
        var byName = await _client.GetAsync("/v1/search?query=portable-kit,not-real");
        var byProv = await _client.GetAsync("/v1/search?query=shelly&by=provides");
        var byDesc = await _client.GetAsync("/v1/search?query=handheld,portable&by=words");

        Assert.That(byName.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(byProv.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(byDesc.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var byNameBody = await byName.Content.ReadAsStringAsync();
        var byProvBody = await byProv.Content.ReadAsStringAsync();
        var byDescBody = await byDesc.Content.ReadAsStringAsync();

        using var byNameDoc = JsonDocument.Parse(byNameBody);
        using var byProvidesDoc = JsonDocument.Parse(byProvBody);
        using var byWordsDoc = JsonDocument.Parse(byDescBody);

        Assert.That(byNameDoc.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(byNameDoc.RootElement[0].GetProperty("name").GetString(), Is.EqualTo("portable-kit"));

        Assert.That(byProvidesDoc.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(byProvidesDoc.RootElement[0].GetProperty("name").GetString(), Is.EqualTo("shelly-bin"));

        Assert.That(byWordsDoc.RootElement.GetArrayLength(), Is.EqualTo(2));
        Assert.That(byWordsDoc.RootElement[0].GetProperty("name").GetString(), Is.EqualTo("portable-pro"));
        Assert.That(byWordsDoc.RootElement[1].GetProperty("name").GetString(), Is.EqualTo("portable-kit"));
    }

    [Test]
    public async Task InvalidPackagesByAndUnknownRouteReturnTextHtml404()
    {
        var invalidBy = await _client.GetAsync("/v1/search?query=shelly&by=unknown");
        var unknown = await _client.GetAsync("/does-not-exist");

        Assert.That(invalidBy.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task MetricsReturnsPrometheusText()
    {
        _ = await _client.GetAsync("/v1/search?query=portable-kit");

        var response = await _client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/plain"));

        Assert.Multiple(() =>
        {
            // The search request above is counted, and the sample index has 3 names / 3 provides.
            Assert.That(body, Does.Match(@"atoll_search_requests_total\{[^}]*\} [1-9]"));
            Assert.That(body, Does.Match("""atoll_index_size\{[^}]*index="names"[^}]*\} 3"""));
            Assert.That(body, Does.Match("""atoll_index_size\{[^}]*index="provides"[^}]*\} 3"""));
            Assert.That(body, Does.Match("""atoll_index_size\{[^}]*index="words"[^}]*\} [1-9]"""));

            // Uptime gauge plus ASP.NET Core request metrics from instrumentation.
            Assert.That(body, Does.Contain("process_uptime_seconds"));
            Assert.That(body, Does.Contain("http_server_request_duration_seconds"));
        });
    }
}