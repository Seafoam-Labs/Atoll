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
    public async Task GetHealth_WithHeadVerb_ReturnsOk()
    {
        var get = await _client.GetAsync("/health", TestContext.Current.CancellationToken);
        var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task GetV1Search_NameProvidesAndWordsQueries_ReturnExpectedNames()
    {
        var byName = await SearchNamesAsync("query=portable-kit,not-real");
        var byProvides = await SearchNamesAsync("query=shelly&by=provides");
        var byWords = await SearchNamesAsync("query=handheld,portable&by=words");

        Assert.Equal(["portable-kit"], byName, StringComparer.Ordinal);
        Assert.Equal(["shelly-bin"], byProvides, StringComparer.Ordinal);
        // Words orders by votes descending: portable-pro carries 20, portable-kit 5.
        Assert.Equal(["portable-pro", "portable-kit"], byWords, StringComparer.Ordinal);
    }

    [Fact]
    public async Task GetV1Search_ByName_IsExactCaseSensitiveAndSubstringFree()
    {
        var exact = await SearchNamesAsync("by=name&query=shelly-bin");
        var wrongCase = await SearchNamesAsync("by=name&query=Shelly-Bin");
        var nearMissPrefix = await SearchNamesAsync("by=name&query=portable");
        var providesValue = await SearchNamesAsync("by=name&query=shelly");
        var unknown = await SearchNamesAsync("by=name&query=not-real");

        Assert.Equal(["shelly-bin"], exact, StringComparer.Ordinal);
        Assert.Empty(wrongCase);
        Assert.Empty(nearMissPrefix);
        Assert.Empty(providesValue);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task GetV1Search_ByWords_RequiresEveryToken()
    {
        var allTokens = await SearchNamesAsync("by=words&query=handheld,emulator");
        var missingToken = await SearchNamesAsync("by=words&query=handheld,missing");
        var wrongCase = await SearchNamesAsync("by=words&query=Handheld");

        Assert.Equal(["portable-pro"], allTokens, StringComparer.Ordinal);
        Assert.Empty(missingToken);
        Assert.Empty(wrongCase);
    }

    [Fact]
    public async Task GetV1Search_ByProvides_IsExactAndFallsBackToSelfName()
    {
        var exact = await SearchNamesAsync("by=provides&query=shelly");
        var nearMissPrefix = await SearchNamesAsync("by=provides&query=shel");
        var selfNameFallback = await SearchNamesAsync("by=provides&query=portable-kit");
        var explicitSelfName = await SearchNamesAsync("by=provides&query=shelly-bin");
        var unknown = await SearchNamesAsync("by=provides&query=not-real");

        Assert.Equal(["shelly-bin"], exact, StringComparer.Ordinal);
        Assert.Empty(nearMissPrefix);
        Assert.Equal(["portable-kit"], selfNameFallback, StringComparer.Ordinal);
        Assert.Empty(explicitSelfName);
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task GetV1Search_QueryBinding_PinsSpacesEncodingRepeatsAndEmpties()
    {
        // Legacy modes never treat a space as a separator; "portable kit" stays one literal key.
        var space = await SearchNamesAsync("by=words&query=portable%20kit");
        // Percent-encoded commas and dashes decode before SearchQuery.TryParse runs.
        var encodedComma = await SearchNamesAsync("by=name&query=portable-kit%2Cnot-real");
        var encodedName = await SearchNamesAsync("by=name&query=shelly%2Dbin");
        // Repeated parameters are comma-joined by the binder, so a miss-first pair still batches.
        var repeated = await SearchNamesAsync("by=name&query=not-real&query=portable-kit");
        var empty = await SearchNamesAsync("by=name&query=");
        var emptySegments = await SearchNamesAsync("by=name&query=%2C%2C%20");
        var missing = await SearchNamesAsync("by=name");

        Assert.Empty(space);
        Assert.Equal(["portable-kit"], encodedComma, StringComparer.Ordinal);
        Assert.Equal(["shelly-bin"], encodedName, StringComparer.Ordinal);
        Assert.Equal(["portable-kit"], repeated, StringComparer.Ordinal);
        Assert.Empty(empty);
        Assert.Empty(emptySegments);
        Assert.Empty(missing);
    }

    [Fact]
    public async Task GetV1Search_RepeatedByValues_CombineAsFlags()
    {
        // "name,words" ORs to Words because Name is 0; an undefined combination is rejected instead.
        var collapsed = await SearchNamesAsync("query=handheld&by=name&by=words");
        var undefined = await _client.GetAsync("/v1/search?query=handheld&by=provides&by=words", TestContext.Current.CancellationToken);

        Assert.Equal(["portable-pro", "portable-kit"], collapsed, StringComparer.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, undefined.StatusCode);
    }

    [Fact]
    public async Task GetV1Search_ByRelevance_ServesRankedBareArray()
    {
        var portable = await SearchNamesAsync("query=portable&by=relevance");
        var upperCase = await SearchNamesAsync("query=SHELLY&by=Relevance");
        var wordTie = await SearchNamesAsync("query=handheld&by=relevance");
        var token = await SearchNamesAsync("query=kit&by=relevance");
        var noHit = await SearchNamesAsync("query=brwose&by=relevance");

        Assert.Multiple(() =>
        {
            // Exact provides (portable-pro) outranks the name prefix (portable-kit).
            Assert.Equal(["portable-pro", "portable-kit"], portable, StringComparer.Ordinal);
            // Case-insensitive name match still finds shelly-bin while case-sensitive provides misses.
            Assert.Equal(["shelly-bin"], upperCase, StringComparer.Ordinal);
            // Pure word-posting tie broken by votes descending (portable-pro 20, portable-kit 5).
            Assert.Equal(["portable-pro", "portable-kit"], wordTie, StringComparer.Ordinal);
            Assert.Equal(["portable-kit"], token, StringComparer.Ordinal);
            Assert.Empty(noHit);
        });
    }

    [Fact]
    public async Task GetV1Search_PlusSeparatorAndLiteralPlus_RankIdentically()
    {
        // QueryStringEnumerable.Decode turns '+' into a space before unescaping, so "portable+pro" is
        // two terms; "%2B" unescapes to a literal '+' that survives as one term, which resolves
        // through the "portable"/"pro" postings its segment splits into and ranks on votes.
        var plusAsSeparator = await SearchNamesAsync("query=portable+pro&by=relevance");
        var plusAsLiteral = await SearchNamesAsync("query=portable%2Bpro&by=relevance");

        Assert.Multiple(() =>
        {
            // Two-term coverage ranks portable-pro (both terms) above portable-kit (prefix only).
            Assert.Equal(["portable-pro", "portable-kit"], plusAsSeparator, StringComparer.Ordinal);
            Assert.Equal(["portable-pro", "portable-kit"], plusAsLiteral, StringComparer.Ordinal);
        });
    }

    [Fact]
    public async Task GetV1Search_RelevanceItem_OmitsScoreAndTierFields()
    {
        var response = await _client.GetAsync("/v1/search?query=portable&by=relevance", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var first = document.RootElement[0];

        Assert.Multiple(() =>
        {
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            Assert.True(first.TryGetProperty("name", out _));
            Assert.False(first.TryGetProperty("score", out _));
            Assert.False(first.TryGetProperty("tier", out _));
        });
    }

    [Fact]
    public async Task GetV1Search_RelevanceQueryOverBound_ReturnsProblemDetails400()
    {
        var overBound = new string('a', 257);

        var response = await _client.GetAsync($"/v1/search?query={overBound}&by=relevance", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // GlobalExceptionHandler maps ArgumentOutOfRangeException to ProblemDetails, message as title;
        // the message carries the framework's "(Parameter 'raw') / Actual value was 257." suffix.
        using var document = JsonDocument.Parse(body);
        Assert.StartsWith(
            "Relevance query must be at most 256 characters.",
            document.RootElement.GetProperty("title").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapEndpoints_UnknownByValueAndUnknownRoute_Return400And404()
    {
        var invalidBy = await _client.GetAsync("/v1/search?query=shelly&by=unknown", TestContext.Current.CancellationToken);
        var unknown = await _client.GetAsync("/does-not-exist", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, invalidBy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task GetMetrics_AfterSearch_ExposesPrometheusAndAtollSamples()
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

    private async Task<string[]> SearchNamesAsync(string queryString)
    {
        var response = await _client.GetAsync($"/v1/search?{queryString}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        return
        [
            .. document.RootElement
                .EnumerateArray()
                .Select(element => element.GetProperty("name").GetString() ?? string.Empty)
        ];
    }
}