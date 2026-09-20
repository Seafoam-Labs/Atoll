using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public class AurRpcEndpointsTests : IDisposable
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
        var byDescription = await Json("/rpc?v=5&type=search&arg=modern");
        var byProvides = await Json("/rpc?v=5&type=search&arg=shelly&by=provides");
        var byCheckDepends = await Json("/rpc?v=5&type=search&arg=bats&by=checkdepends");

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
        var info = await Json("/rpc/v5/info/shelly-bin");
        var search = await Json("/rpc/v5/search/portable?by=name");
        var suggestions = await Json("/rpc/v5/suggest/port");
        var packageBaseSuggestions = await Json("/rpc/v5/suggest-pkgbase/shel");

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
        var body = await Json(path);

        Assert.Multiple(() =>
        {
            Assert.Equal("error", body.GetProperty("type").GetString());
            Assert.Equal(0, body.GetProperty("resultcount").GetInt32());
            Assert.Equal(expectedError, body.GetProperty("error").GetString());
        });
    }

    private async Task<JsonElement> Json(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
