using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public sealed class OpenApiEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly ApiTestFactory _factory;

    public OpenApiEndpointsTests()
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
    public async Task OpenApiSchemaExposesTypedEndpointsAndComponents()
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.True(root.TryGetProperty("openapi", out var version));
            Assert.StartsWith("3.", version.GetString(), StringComparison.Ordinal);

            var paths = root.GetProperty("paths");
            Assert.True(paths.TryGetProperty("/v1/search", out _));
            Assert.True(paths.TryGetProperty("/v1/packages", out var packagesPath));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}", out var packagePath));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}/seed", out var seedPath));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}/versions", out _));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}/security", out var securityPath));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}/security/rescan", out var rescanPath));
            Assert.True(paths.TryGetProperty("/v1/packages/{name}/tarball", out var tarballPath));
            Assert.True(paths.TryGetProperty("/rpc", out _));
            Assert.True(paths.TryGetProperty("/rpc/v5/info", out _));
            Assert.True(paths.TryGetProperty("/rpc/v5/info/{arg}", out var rpcInfoArgPath));
            Assert.True(paths.TryGetProperty("/rpc/v5/search/{arg}", out _));
            Assert.True(paths.TryGetProperty("/rpc/v5/suggest/{arg}", out _));

            // Verify status codes on endpoints
            var getIndexResponses = packagesPath.GetProperty("get").GetProperty("responses");
            Assert.True(getIndexResponses.TryGetProperty("200", out _));
            Assert.True(getIndexResponses.TryGetProperty("400", out _));
            Assert.Equal("#/components/schemas/PackageIndexResponse", JsonSchemaReference(getIndexResponses, "200"));

            var getSecurityResponses = securityPath.GetProperty("get").GetProperty("responses");
            Assert.True(getSecurityResponses.TryGetProperty("200", out _));
            Assert.True(getSecurityResponses.TryGetProperty("404", out _));
            Assert.Equivalent(new[]
            {
                "#/components/schemas/PackageSecurityHistoryResponse",
                "#/components/schemas/PackageSecurityRevisionResponse"
            }, JsonSchemaReferences(getSecurityResponses, "200"), strict: true);

            var postRescanResponses = rescanPath.GetProperty("post").GetProperty("responses");
            Assert.True(postRescanResponses.TryGetProperty("202", out _));
            Assert.True(postRescanResponses.TryGetProperty("403", out _));
            Assert.True(postRescanResponses.TryGetProperty("404", out _));

            // The tarball is deliberately ungated (human review surface): 200/404 only, no 403.
            var getTarballResponses = tarballPath.GetProperty("get").GetProperty("responses");
            Assert.True(getTarballResponses.TryGetProperty("200", out _));
            Assert.True(getTarballResponses.TryGetProperty("404", out _));
            Assert.True(
                getTarballResponses.GetProperty("200").GetProperty("content").TryGetProperty("application/gzip", out _));

            var getPackageResponses = packagePath.GetProperty("get").GetProperty("responses");
            Assert.True(getPackageResponses.TryGetProperty("200", out _));
            Assert.True(getPackageResponses.TryGetProperty("403", out _));

            var deletePackageResponses = packagePath.GetProperty("delete").GetProperty("responses");
            Assert.True(deletePackageResponses.TryGetProperty("204", out _));
            Assert.True(deletePackageResponses.TryGetProperty("403", out _));

            var seedPackageResponses = seedPath.GetProperty("post").GetProperty("responses");
            Assert.True(seedPackageResponses.TryGetProperty("201", out _));
            Assert.True(seedPackageResponses.TryGetProperty("403", out _));

            var rpcInfoResponses = rpcInfoArgPath.GetProperty("get").GetProperty("responses");
            Assert.Equal("#/components/schemas/AurRpcResponse", JsonSchemaReference(rpcInfoResponses, "200"));


            // Verify schemas in components
            var schemas = root.GetProperty("components").GetProperty("schemas");
            Assert.True(schemas.TryGetProperty("AurPackageMetadata", out _));
            Assert.True(schemas.TryGetProperty("AurRpcResponse", out _));
            Assert.True(schemas.TryGetProperty("AurRpcPackage", out _));
            Assert.True(schemas.TryGetProperty("PackageFiles", out _));
            Assert.True(schemas.TryGetProperty("PackageVersion", out _));
            Assert.True(schemas.TryGetProperty("PackageIndexResponse", out _));
            Assert.True(schemas.TryGetProperty("PackageIndexEntry", out var indexEntrySchema));
            var indexEntryProperties = indexEntrySchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            // upstreamPackageBase was dropped once the wire contract had a populated packageBase.
            Assert.DoesNotContain("upstreamPackageBase", indexEntryProperties);
            Assert.Superset(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "name", "description", "version", "numVotes", "popularity", "outOfDate", "url", "maintainer",
                    "packageBase", "firstSubmitted", "lastModified", "license", "depends", "makeDepends",
                    "optDepends", "provides"
                },
                indexEntryProperties);
            Assert.True(schemas.TryGetProperty("PackageSecurityHistoryResponse", out _));
            Assert.True(schemas.TryGetProperty("PackageSecurityRevisionResponse", out _));
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