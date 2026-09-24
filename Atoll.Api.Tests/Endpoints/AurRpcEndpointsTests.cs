using System.Net;
using System.Text.Json;
using Atoll.Api.Services.Catalog.Rpc;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public sealed class AurRpcEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public AurRpcEndpointsTests()
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
    public async Task LegacyInfo_returns_aurweb_v5_contract_and_custom_clone_path()
    {
        var response = await _client.GetAsync("/rpc?v=5&type=info&arg[]=shelly-bin&arg[]=missing", TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = body.RootElement;
        var package = root.GetProperty("results")[0];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Equal(5, root.GetProperty("version").GetInt32());
            Assert.Equal("multiinfo", root.GetProperty("type").GetString());
            Assert.Equal(1, root.GetProperty("resultcount").GetInt32());
            Assert.Equal(101, package.GetProperty("ID").GetInt64());
            Assert.Equal("shelly-bin", package.GetProperty("Name").GetString());
            Assert.Equal("shelly", package.GetProperty("PackageBase").GetString());
            Assert.Equal("/shelly.git", package.GetProperty("URLPath").GetString());
            Assert.Equal("pacman>=6", package.GetProperty("Depends")[0].GetString());
            Assert.Equal("bats", package.GetProperty("CheckDepends")[0].GetString());
            Assert.Equal("atoll-test", package.GetProperty("Groups")[0].GetString());
            Assert.Equal("shelly-old", package.GetProperty("Replaces")[0].GetString());
        });
    }

    [Fact]
    public async Task LegacyInfo_accepts_paru_form_encoded_post_requests()
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("v", "5"),
            new KeyValuePair<string, string>("type", "info"),
            new KeyValuePair<string, string>("arg[]", "shelly-bin"),
            new KeyValuePair<string, string>("arg[]", "missing")
        ]);

        var response = await _client.PostAsync("/rpc", content, TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Multiple(() =>
        {
            Assert.Equal("multiinfo", body.RootElement.GetProperty("type").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("resultcount").GetInt32());
            Assert.Equal("shelly-bin", body.RootElement.GetProperty("results")[0].GetProperty("Name").GetString());
        });
    }

    [Fact]
    public async Task LegacySearch_supports_default_and_relation_fields()
    {
        var byDescription = await JsonAsync("/rpc?v=5&type=search&arg=modern");
        var byProvides = await JsonAsync("/rpc?v=5&type=search&arg=shelly&by=provides");
        var byCheckDepends = await JsonAsync("/rpc?v=5&type=search&arg=bats&by=checkdepends");

        Assert.Multiple(() =>
        {
            Assert.Equal("shelly-bin", byDescription.GetProperty("results")[0].GetProperty("Name").GetString());
            Assert.Equal(1, byProvides.GetProperty("resultcount").GetInt32());
            Assert.Equal(1, byCheckDepends.GetProperty("resultcount").GetInt32());
        });
    }

    [Fact]
    public async Task PathRpc_and_suggestions_are_supported()
    {
        var info = await JsonAsync("/rpc/v5/info/shelly-bin");
        var search = await JsonAsync("/rpc/v5/search/portable?by=name");
        var suggestions = await JsonAsync("/rpc/v5/suggest/port");
        var packageBaseSuggestions = await JsonAsync("/rpc/v5/suggest-pkgbase/shel");

        Assert.Multiple(() =>
        {
            Assert.Equal("multiinfo", info.GetProperty("type").GetString());
            Assert.Equal(2, search.GetProperty("resultcount").GetInt32());
            Assert.Equal(2, suggestions.GetArrayLength());
            Assert.Equal("shelly", packageBaseSuggestions[0].GetString());
        });
    }

    [Theory]
    [InlineData("/rpc?type=info&arg=shelly-bin", "Please specify an API version.")]
    [InlineData("/rpc?v=4&type=info&arg=shelly-bin", "Invalid version specified.")]
    [InlineData("/rpc?v=5&type=search&arg=x", "Query arg too small.")]
    [InlineData("/rpc?v=5&type=search&arg=shelly&by=unknown", "Incorrect by field specified.")]
    public async Task Invalid_requests_return_aurweb_error_envelopes(string path, string expectedError)
    {
        var body = await JsonAsync(path);

        Assert.Multiple(() =>
        {
            Assert.Equal("error", body.GetProperty("type").GetString());
            Assert.Equal(0, body.GetProperty("resultcount").GetInt32());
            Assert.Equal(expectedError, body.GetProperty("error").GetString());
        });
    }

    [Fact]
    public async Task LegacyInfo_result_carries_the_exact_uppercase_wire_field_set()
    {
        var body = await JsonAsync("/rpc?v=5&type=info&arg=shelly-bin");

        var fields = body.GetProperty("results")[0].EnumerateObject().Select(property => property.Name);

        Assert.Equal(
            [
                "ID", "Name", "PackageBaseID", "PackageBase", "Version", "Description", "URL",
                "NumVotes", "Popularity", "OutOfDate", "Maintainer", "FirstSubmitted", "LastModified",
                "URLPath", "Depends", "CheckDepends", "Provides", "Replaces", "Groups", "License", "Keywords"
            ],
            fields,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task LegacySearch_field_predicates_match_aurweb_semantics()
    {
        var providesExact = await JsonAsync("/rpc?v=5&type=search&arg=shel&by=provides");
        var providesHit = await JsonAsync("/rpc?v=5&type=search&arg=shelly&by=provides");
        var dependsStripped = await JsonAsync("/rpc?v=5&type=search&arg=pacman&by=depends");
        var dependsWithConstraint = await JsonAsync("/rpc?v=5&type=search&arg=pacman%3E%3D6&by=depends");
        var maintainerExact = await JsonAsync("/rpc?v=5&type=search&arg=alice&by=maintainer");
        var maintainerSubstring = await JsonAsync("/rpc?v=5&type=search&arg=ali&by=maintainer");

        Assert.Multiple(() =>
        {
            Assert.Equal(0, providesExact.GetProperty("resultcount").GetInt32());
            Assert.Equal(1, providesHit.GetProperty("resultcount").GetInt32());
            // Version constraints are stripped from both sides of the comparison.
            Assert.Equal(1, dependsStripped.GetProperty("resultcount").GetInt32());
            Assert.Equal(0, dependsWithConstraint.GetProperty("resultcount").GetInt32());
            Assert.Equal(1, maintainerExact.GetProperty("resultcount").GetInt32());
            Assert.Equal(0, maintainerSubstring.GetProperty("resultcount").GetInt32());
        });
    }

    [Fact]
    public async Task LegacySearch_orders_name_matches_ordinal()
    {
        await using var factory = new ApiTestFactory { Index = TestData.IndexFromNames(["alpha", "ALPHA", "alpha-b", "alpha_a"]) };
        using var client = factory.CreateClient();

        var body = await JsonAsync(client, "/rpc?v=5&type=search&arg=alph&by=name");

        var names = body.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("Name").GetString() ?? string.Empty);

        Assert.Equal(["ALPHA", "alpha", "alpha-b", "alpha_a"], names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task LegacySearch_succeeds_at_five_thousand_matches()
    {
        await using var factory = BulkFactory(AurRpcService.MaxResults);
        using var client = factory.CreateClient();

        var body = await JsonAsync(client, "/rpc?v=5&type=search&arg=bulk&by=name");

        var results = body.GetProperty("results");
        Assert.Multiple(() =>
        {
            Assert.Equal(AurRpcService.MaxResults, body.GetProperty("resultcount").GetInt32());
            Assert.Equal(AurRpcService.MaxResults, results.GetArrayLength());
            Assert.Equal("bulk-0000", results[0].GetProperty("Name").GetString());
            Assert.Equal("bulk-4999", results[results.GetArrayLength() - 1].GetProperty("Name").GetString());
        });
    }

    [Fact]
    public async Task LegacySearch_errors_when_matches_exceed_five_thousand()
    {
        await using var factory = BulkFactory(AurRpcService.MaxResults + 1);
        using var client = factory.CreateClient();

        var body = await JsonAsync(client, "/rpc?v=5&type=search&arg=bulk&by=name");

        Assert.Multiple(() =>
        {
            Assert.Equal("error", body.GetProperty("type").GetString());
            Assert.Equal(0, body.GetProperty("resultcount").GetInt32());
            Assert.Equal("Too many package results.", body.GetProperty("error").GetString());
        });
    }

    private static ApiTestFactory BulkFactory(int count) => new()
    {
        Index = TestData.IndexFromNames(Enumerable.Range(0, count).Select(i => $"bulk-{i:0000}"))
    };

    private Task<JsonElement> JsonAsync(string path) => JsonAsync(_client, path);

    private static async Task<JsonElement> JsonAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return document.RootElement.Clone();
    }
}
