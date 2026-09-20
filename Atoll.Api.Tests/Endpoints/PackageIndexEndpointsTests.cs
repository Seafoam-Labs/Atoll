using System.Net;
using System.Text.Json;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Tests.Support;
using Xunit;

namespace Atoll.Api.Tests.Endpoints;

public class PackageIndexEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly SecurityTestFactory _factory;

    public PackageIndexEndpointsTests()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Index_returns_ordered_pages_with_envelope_metadata()
    {
        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg", "b-banana", "d-date", "f-fig", "g-grape" })
            await SeedAsync(name);

        await AppendAsync("e-egg", "rev-2");

        var response = await _client.GetAsync("/v1/packages?limit=3&page=2", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.Equal(2, root.GetProperty("page").GetInt32());
            Assert.Equal(3, root.GetProperty("limit").GetInt32());
            Assert.Equal(7, root.GetProperty("totalItems").GetInt64());
            Assert.Equal(3, root.GetProperty("totalPages").GetInt32());

            var items = root.GetProperty("items");
            Assert.Equal(3, items.GetArrayLength());
            Assert.Equal("d-date", items[0].GetProperty("name").GetString());
            Assert.Equal("e-egg", items[1].GetProperty("name").GetString());
            Assert.Equal("f-fig", items[2].GetProperty("name").GetString());

            Assert.Equal("rev-1", items[0].GetProperty("headRevisionId").GetString());
            Assert.Equal(1, items[0].GetProperty("revisionCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("upstreamPackageBase").ValueKind);
            Assert.True(items[0].TryGetProperty("createdAt", out var createdAt));
            Assert.True(createdAt.GetDateTimeOffset() > DateTimeOffset.MinValue);
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("numVotes").ValueKind);

            Assert.Equal("rev-2", items[1].GetProperty("headRevisionId").GetString());
            Assert.Equal(2, items[1].GetProperty("revisionCount").GetInt32());
        });
    }

    [Fact]
    public async Task Index_applies_default_page_and_limit()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.Equal(1, root.GetProperty("page").GetInt32());
            Assert.Equal(50, root.GetProperty("limit").GetInt32());
            Assert.Equal(3, root.GetProperty("totalItems").GetInt64());
            Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
            Assert.Equal(3, root.GetProperty("items").GetArrayLength());
            Assert.Equal("a-apple", root.GetProperty("items")[0].GetProperty("name").GetString());
        });
    }

    [Fact]
    public async Task Index_returns_400_for_out_of_range_or_malformed_parameters()
    {
        var zeroPage = await _client.GetAsync("/v1/packages?page=0", TestContext.Current.CancellationToken);
        var zeroLimit = await _client.GetAsync("/v1/packages?limit=0", TestContext.Current.CancellationToken);
        var overMaxLimit = await _client.GetAsync("/v1/packages?limit=201", TestContext.Current.CancellationToken);
        var malformedPage = await _client.GetAsync("/v1/packages?page=abc", TestContext.Current.CancellationToken);
        var malformedSort = await _client.GetAsync("/v1/packages?sortBy=bogus", TestContext.Current.CancellationToken);
        var malformedOrder = await _client.GetAsync("/v1/packages?order=bogus", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.BadRequest, zeroPage.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, zeroLimit.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, overMaxLimit.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, malformedPage.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, malformedSort.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, malformedOrder.StatusCode);
        });
    }

    [Fact]
    public async Task Index_applies_order_parameter_to_name_sort()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var descending = await _client.GetAsync("/v1/packages?sortBy=name&order=desc", TestContext.Current.CancellationToken);
        var ascending = await _client.GetAsync("/v1/packages?sortBy=name&order=ASC", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, descending.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ascending.StatusCode);
        });

        var descendingBody = await descending.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var descendingDoc = JsonDocument.Parse(descendingBody);
        var descendingNames = descendingDoc.RootElement.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();

        var ascendingBody = await ascending.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var ascendingDoc = JsonDocument.Parse(ascendingBody);
        var ascendingNames = ascendingDoc.RootElement.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.Equal(new[] { "c-carrot", "b-banana", "a-apple" }, descendingNames);
            Assert.Equal(new[] { "a-apple", "b-banana", "c-carrot" }, ascendingNames);
        });
    }

    [Fact]
    public async Task Index_sorts_votes_ascending_by_default()
    {
        foreach (var name in new[] { "portable-kit", "portable-pro", "shelly-bin", "a-absent" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages?sortBy=votes&limit=4", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("items")
            .EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();

        // Sample index votes: portable-pro 20, shelly-bin 10, portable-kit 5; "a-absent" ranks as zero.
        Assert.Equal(new[] { "a-absent", "portable-kit", "shelly-bin", "portable-pro" }, names);
    }

    [Fact]
    public async Task Index_sorts_by_votes_descending_across_pages()
    {
        foreach (var name in new[] { "portable-kit", "portable-pro", "shelly-bin", "a-absent" })
            await SeedAsync(name);

        var firstPage = await _client.GetAsync("/v1/packages?sortBy=votes&order=desc&limit=2&page=1", TestContext.Current.CancellationToken);
        var secondPage = await _client.GetAsync("/v1/packages?sortBy=VOTES&order=desc&limit=2&page=2", TestContext.Current.CancellationToken);

        Assert.Multiple(() =>
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            Assert.Equal(HttpStatusCode.OK, secondPage.StatusCode);
        });

        var firstBody = await firstPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var firstDoc = JsonDocument.Parse(firstBody);
        var firstItems = firstDoc.RootElement.GetProperty("items");

        var secondBody = await secondPage.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var secondDoc = JsonDocument.Parse(secondBody);
        var secondItems = secondDoc.RootElement.GetProperty("items");

        Assert.Multiple(() =>
        {
            // Sample index votes: portable-pro 20, shelly-bin 10, portable-kit 5;
            // "a-absent" is not in the index and ranks as zero votes on the last page.
            Assert.Equal("portable-pro", firstItems[0].GetProperty("name").GetString());
            Assert.Equal("shelly-bin", firstItems[1].GetProperty("name").GetString());
            Assert.Equal("portable-kit", secondItems[0].GetProperty("name").GetString());
            Assert.Equal("a-absent", secondItems[1].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, secondItems[1].GetProperty("numVotes").ValueKind);
        });
    }

    [Fact]
    public async Task Index_beyond_last_page_returns_empty_items()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages?page=99&limit=3", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.Equal(0, root.GetProperty("items").GetArrayLength());
            Assert.Equal(99, root.GetProperty("page").GetInt32());
            Assert.Equal(3, root.GetProperty("totalItems").GetInt64());
            Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
        });
    }

    [Fact]
    public async Task Index_with_int_max_page_returns_empty_items_without_overflow()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages?page=2147483647&limit=200", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(0, doc.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Index_returns_empty_envelope_for_empty_corpus()
    {
        using var factory = new SecurityTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/packages", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.Equal(0, root.GetProperty("items").GetArrayLength());
            Assert.Equal(1, root.GetProperty("page").GetInt32());
            Assert.Equal(50, root.GetProperty("limit").GetInt32());
            Assert.Equal(0, root.GetProperty("totalItems").GetInt64());
            Assert.Equal(0, root.GetProperty("totalPages").GetInt32());
        });
    }

    [Fact]
    public async Task Index_rows_carry_catalog_fields_when_names_are_in_the_index()
    {
        await SeedAsync("shelly-bin");
        await SeedAsync("a-apple");

        var response = await _client.GetAsync("/v1/packages?limit=2", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");

        Assert.Equal(2, items.GetArrayLength());

        var apple = items[0];
        Assert.Multiple(() =>
        {
            Assert.Equal("a-apple", apple.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, apple.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, apple.GetProperty("version").ValueKind);
            Assert.Equal(JsonValueKind.Null, apple.GetProperty("numVotes").ValueKind);
            Assert.Equal(JsonValueKind.Null, apple.GetProperty("popularity").ValueKind);
            Assert.Equal(JsonValueKind.Null, apple.GetProperty("outOfDate").ValueKind);
        });

        var shelly = items[1];
        Assert.Multiple(() =>
        {
            Assert.Equal("shelly-bin", shelly.GetProperty("name").GetString());
            Assert.Equal("Shelly: A Modern Arch Package Manager (prebuilt binary)",
                shelly.GetProperty("description").GetString());
            Assert.Equal("1.2.3-1", shelly.GetProperty("version").GetString());
            Assert.Equal(10, shelly.GetProperty("numVotes").GetInt32());
            Assert.Equal(0, shelly.GetProperty("popularity").GetDouble());
            Assert.Equal(1735689600, shelly.GetProperty("outOfDate").GetInt64());
        });
    }

    private async Task SeedAsync(string name, string revisionId = "rev-1")
    {
        var now = DateTimeOffset.UtcNow;
        await _factory.Repository.InsertSeedAsync(
            new PackageDocument
            {
                Id = name,
                PackageName = name,
                CreatedAt = now,
                UpdatedAt = now,
                HeadRevisionId = revisionId,
                Revisions =
                [
                    new PackageRevisionDocument { RevisionId = revisionId, CreatedAt = now, Author = "test", Message = "seed" }
                ]
            },
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(name, revisionId),
                PackageName = name,
                RevisionId = revisionId,
                CreatedAt = now,
                Author = "test",
                Message = "seed",
                Files = new Dictionary<string, PackageFile>
                {
                    ["PKGBUILD"] = new() { Content = $"pkgname={name}\n", Size = 8 + name.Length, Hash = revisionId }
                }
            });
    }

    private async Task AppendAsync(string name, string revisionId)
    {
        var now = DateTimeOffset.UtcNow;
        await _factory.Repository.AppendRevisionAsync(
            name,
            new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(name, revisionId),
                PackageName = name,
                RevisionId = revisionId,
                CreatedAt = now,
                Author = "test",
                Message = "append",
                Files = new Dictionary<string, PackageFile>
                {
                    ["PKGBUILD"] = new() { Content = $"pkgname={name}\n# {revisionId}\n", Size = 10 + name.Length, Hash = revisionId }
                }
            },
            10);
    }
}
