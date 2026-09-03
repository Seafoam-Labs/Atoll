using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class OpenApiEndpointsTests
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
    public async Task OpenApiSchemaExposesTypedEndpointsAndComponents()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.TryGetProperty("openapi", out var version), Is.True);
            Assert.That(version.GetString(), Does.StartWith("3."));

            var paths = root.GetProperty("paths");
            Assert.That(paths.TryGetProperty("/v1/search", out _), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages", out var packagesPath), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages/{name}", out var packagePath), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages/{name}/seed", out var seedPath), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages/{name}/versions", out _), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages/{name}/security", out var securityPath), Is.True);
            Assert.That(paths.TryGetProperty("/v1/packages/{name}/security/rescan", out var rescanPath), Is.True);
            Assert.That(paths.TryGetProperty("/rpc", out _), Is.True);
            Assert.That(paths.TryGetProperty("/rpc/v5/info", out _), Is.True);
            Assert.That(paths.TryGetProperty("/rpc/v5/info/{arg}", out var rpcInfoArgPath), Is.True);
            Assert.That(paths.TryGetProperty("/rpc/v5/search/{arg}", out _), Is.True);
            Assert.That(paths.TryGetProperty("/rpc/v5/suggest/{arg}", out _), Is.True);

            // Verify status codes on endpoints
            var getIndexResponses = packagesPath.GetProperty("get").GetProperty("responses");
            Assert.That(getIndexResponses.TryGetProperty("200", out _), Is.True);
            Assert.That(getIndexResponses.TryGetProperty("400", out _), Is.True);
            Assert.That(JsonSchemaReference(getIndexResponses, "200"),
                Is.EqualTo("#/components/schemas/PackageIndexResponse"));

            var getSecurityResponses = securityPath.GetProperty("get").GetProperty("responses");
            Assert.That(getSecurityResponses.TryGetProperty("200", out _), Is.True);
            Assert.That(getSecurityResponses.TryGetProperty("404", out _), Is.True);
            Assert.That(JsonSchemaReferences(getSecurityResponses, "200"), Is.EquivalentTo([
                "#/components/schemas/PackageSecurityHistoryResponse",
                "#/components/schemas/PackageSecurityRevisionResponse"
            ]));

            var postRescanResponses = rescanPath.GetProperty("post").GetProperty("responses");
            Assert.That(postRescanResponses.TryGetProperty("202", out _), Is.True);
            Assert.That(postRescanResponses.TryGetProperty("403", out _), Is.True);
            Assert.That(postRescanResponses.TryGetProperty("404", out _), Is.True);

            var getPackageResponses = packagePath.GetProperty("get").GetProperty("responses");
            Assert.That(getPackageResponses.TryGetProperty("200", out _), Is.True);
            Assert.That(getPackageResponses.TryGetProperty("403", out _), Is.True);

            var deletePackageResponses = packagePath.GetProperty("delete").GetProperty("responses");
            Assert.That(deletePackageResponses.TryGetProperty("204", out _), Is.True);
            Assert.That(deletePackageResponses.TryGetProperty("403", out _), Is.True);

            var seedPackageResponses = seedPath.GetProperty("post").GetProperty("responses");
            Assert.That(seedPackageResponses.TryGetProperty("201", out _), Is.True);
            Assert.That(seedPackageResponses.TryGetProperty("403", out _), Is.True);

            var rpcInfoResponses = rpcInfoArgPath.GetProperty("get").GetProperty("responses");
            Assert.That(JsonSchemaReference(rpcInfoResponses, "200"),
                Is.EqualTo("#/components/schemas/AurRpcResponse"));


            // Verify schemas in components
            var schemas = root.GetProperty("components").GetProperty("schemas");
            Assert.That(schemas.TryGetProperty("AurPackageMetadata", out _), Is.True);
            Assert.That(schemas.TryGetProperty("AurRpcResponse", out _), Is.True);
            Assert.That(schemas.TryGetProperty("AurRpcPackage", out _), Is.True);
            Assert.That(schemas.TryGetProperty("PackageFiles", out _), Is.True);
            Assert.That(schemas.TryGetProperty("PackageVersion", out _), Is.True);
            Assert.That(schemas.TryGetProperty("PackageIndexResponse", out _), Is.True);
            Assert.That(schemas.TryGetProperty("PackageIndexEntry", out var indexEntrySchema), Is.True);
            Assert.That(
                indexEntrySchema.GetProperty("properties").EnumerateObject().Select(property => property.Name),
                Is.SupersetOf(["name", "description", "version", "numVotes", "popularity", "outOfDate"]));
            Assert.That(schemas.TryGetProperty("PackageSecurityHistoryResponse", out _), Is.True);
            Assert.That(schemas.TryGetProperty("PackageSecurityRevisionResponse", out _), Is.True);
        });
    }

    private static JsonElement JsonSchema(JsonElement responses, string statusCode)
    {
        return responses.GetProperty(statusCode)
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
    }

    private static string? JsonSchemaReference(JsonElement responses, string statusCode)
    {
        return JsonSchema(responses, statusCode).GetProperty("$ref").GetString();
    }

    private static string?[] JsonSchemaReferences(JsonElement responses, string statusCode)
    {
        return
        [
            .. JsonSchema(responses, statusCode)
                .GetProperty("oneOf")
                .EnumerateArray()
                .Select(schema => schema.GetProperty("$ref").GetString())
        ];
    }
}