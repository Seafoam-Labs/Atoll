using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class AurRpcEndpointsTests
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
    public async Task LegacyInfo_returns_aurweb_v5_contract_and_custom_clone_path()
    {
        var response = await _client.GetAsync("/rpc?v=5&type=info&arg[]=shelly-bin&arg[]=missing");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var package = root.GetProperty("results")[0];

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(5));
            Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("multiinfo"));
            Assert.That(root.GetProperty("resultcount").GetInt32(), Is.EqualTo(1));
            Assert.That(package.GetProperty("ID").GetInt64(), Is.EqualTo(101));
            Assert.That(package.GetProperty("Name").GetString(), Is.EqualTo("shelly-bin"));
            Assert.That(package.GetProperty("PackageBase").GetString(), Is.EqualTo("shelly"));
            Assert.That(package.GetProperty("URLPath").GetString(), Is.EqualTo("/shelly.git"));
            Assert.That(package.GetProperty("Depends")[0].GetString(), Is.EqualTo("pacman>=6"));
            Assert.That(package.GetProperty("CheckDepends")[0].GetString(), Is.EqualTo("bats"));
            Assert.That(package.GetProperty("Groups")[0].GetString(), Is.EqualTo("atoll-test"));
            Assert.That(package.GetProperty("Replaces")[0].GetString(), Is.EqualTo("shelly-old"));
        });
    }

    [Test]
    public async Task LegacyInfo_accepts_paru_form_encoded_post_requests()
    {
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("v", "5"),
            new KeyValuePair<string, string>("type", "info"),
            new KeyValuePair<string, string>("arg[]", "shelly-bin"),
            new KeyValuePair<string, string>("arg[]", "missing")
        ]);

        var response = await _client.PostAsync("/rpc", content);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.Multiple(() =>
        {
            Assert.That(body.RootElement.GetProperty("type").GetString(), Is.EqualTo("multiinfo"));
            Assert.That(body.RootElement.GetProperty("resultcount").GetInt32(), Is.EqualTo(1));
            Assert.That(body.RootElement.GetProperty("results")[0].GetProperty("Name").GetString(),
                Is.EqualTo("shelly-bin"));
        });
    }

    [Test]
    public async Task LegacySearch_supports_default_and_relation_fields()
    {
        var byDescription = await Json("/rpc?v=5&type=search&arg=modern");
        var byProvides = await Json("/rpc?v=5&type=search&arg=shelly&by=provides");
        var byCheckDepends = await Json("/rpc?v=5&type=search&arg=bats&by=checkdepends");

        Assert.Multiple(() =>
        {
            Assert.That(byDescription.GetProperty("results")[0].GetProperty("Name").GetString(),
                Is.EqualTo("shelly-bin"));
            Assert.That(byProvides.GetProperty("resultcount").GetInt32(), Is.EqualTo(1));
            Assert.That(byCheckDepends.GetProperty("resultcount").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task PathRpc_and_suggestions_are_supported()
    {
        var info = await Json("/rpc/v5/info/shelly-bin");
        var search = await Json("/rpc/v5/search/portable?by=name");
        var suggestions = await Json("/rpc/v5/suggest/port");
        var packageBaseSuggestions = await Json("/rpc/v5/suggest-pkgbase/shel");

        Assert.Multiple(() =>
        {
            Assert.That(info.GetProperty("type").GetString(), Is.EqualTo("multiinfo"));
            Assert.That(search.GetProperty("resultcount").GetInt32(), Is.EqualTo(2));
            Assert.That(suggestions.GetArrayLength(), Is.EqualTo(2));
            Assert.That(packageBaseSuggestions[0].GetString(), Is.EqualTo("shelly"));
        });
    }

    [TestCase("/rpc?type=info&arg=shelly-bin", "Please specify an API version.")]
    [TestCase("/rpc?v=4&type=info&arg=shelly-bin", "Invalid version specified.")]
    [TestCase("/rpc?v=5&type=search&arg=x", "Query arg too small.")]
    [TestCase("/rpc?v=5&type=search&arg=shelly&by=unknown", "Incorrect by field specified.")]
    public async Task Invalid_requests_return_aurweb_error_envelopes(string path, string expectedError)
    {
        var body = await Json(path);

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("type").GetString(), Is.EqualTo("error"));
            Assert.That(body.GetProperty("resultcount").GetInt32(), Is.Zero);
            Assert.That(body.GetProperty("error").GetString(), Is.EqualTo(expectedError));
        });
    }

    private async Task<JsonElement> Json(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
