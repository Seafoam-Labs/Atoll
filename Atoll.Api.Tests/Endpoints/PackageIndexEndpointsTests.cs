using System.Net;
using System.Text.Json;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Tests.Support;
using NUnit.Framework;

namespace Atoll.Api.Tests.Endpoints;

public class PackageIndexEndpointsTests
{
    private HttpClient _client = null!;
    private SecurityTestFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new SecurityTestFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task Index_returns_ordered_pages_with_envelope_metadata()
    {
        foreach (var name in new[] { "c-carrot", "a-apple", "e-egg", "b-banana", "d-date", "f-fig", "g-grape" })
            await SeedAsync(name);

        await AppendAsync("e-egg", "rev-2");

        var response = await _client.GetAsync("/v1/packages?limit=3&page=2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(2));
            Assert.That(root.GetProperty("limit").GetInt32(), Is.EqualTo(3));
            Assert.That(root.GetProperty("totalItems").GetInt64(), Is.EqualTo(7));
            Assert.That(root.GetProperty("totalPages").GetInt32(), Is.EqualTo(3));

            var items = root.GetProperty("items");
            Assert.That(items.GetArrayLength(), Is.EqualTo(3));
            Assert.That(items[0].GetProperty("name").GetString(), Is.EqualTo("d-date"));
            Assert.That(items[1].GetProperty("name").GetString(), Is.EqualTo("e-egg"));
            Assert.That(items[2].GetProperty("name").GetString(), Is.EqualTo("f-fig"));

            Assert.That(items[0].GetProperty("headRevisionId").GetString(), Is.EqualTo("rev-1"));
            Assert.That(items[0].GetProperty("revisionCount").GetInt32(), Is.EqualTo(1));
            Assert.That(items[0].GetProperty("upstreamPackageBase").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(items[0].TryGetProperty("createdAt", out var createdAt), Is.True);
            Assert.That(createdAt.GetDateTimeOffset(), Is.GreaterThan(DateTimeOffset.MinValue));
            Assert.That(items[0].GetProperty("description").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(items[0].GetProperty("numVotes").ValueKind, Is.EqualTo(JsonValueKind.Null));

            Assert.That(items[1].GetProperty("headRevisionId").GetString(), Is.EqualTo("rev-2"));
            Assert.That(items[1].GetProperty("revisionCount").GetInt32(), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Index_applies_default_page_and_limit()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("limit").GetInt32(), Is.EqualTo(50));
            Assert.That(root.GetProperty("totalItems").GetInt64(), Is.EqualTo(3));
            Assert.That(root.GetProperty("totalPages").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("items").GetArrayLength(), Is.EqualTo(3));
            Assert.That(root.GetProperty("items")[0].GetProperty("name").GetString(), Is.EqualTo("a-apple"));
        });
    }

    [Test]
    public async Task Index_returns_400_for_out_of_range_or_malformed_parameters()
    {
        var zeroPage = await _client.GetAsync("/v1/packages?page=0");
        var zeroLimit = await _client.GetAsync("/v1/packages?limit=0");
        var overMaxLimit = await _client.GetAsync("/v1/packages?limit=201");
        var malformedPage = await _client.GetAsync("/v1/packages?page=abc");

        Assert.Multiple(() =>
        {
            Assert.That(zeroPage.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(zeroLimit.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(overMaxLimit.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(malformedPage.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        });
    }

    [Test]
    public async Task Index_beyond_last_page_returns_empty_items()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages?page=99&limit=3");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("items").GetArrayLength(), Is.Zero);
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(99));
            Assert.That(root.GetProperty("totalItems").GetInt64(), Is.EqualTo(3));
            Assert.That(root.GetProperty("totalPages").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Index_with_int_max_page_returns_empty_items_without_overflow()
    {
        foreach (var name in new[] { "b-banana", "a-apple", "c-carrot" })
            await SeedAsync(name);

        var response = await _client.GetAsync("/v1/packages?page=2147483647&limit=200");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        Assert.That(doc.RootElement.GetProperty("items").GetArrayLength(), Is.Zero);
    }

    [Test]
    public async Task Index_returns_empty_envelope_for_empty_corpus()
    {
        using var factory = new SecurityTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/v1/packages");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.That(root.GetProperty("items").GetArrayLength(), Is.Zero);
            Assert.That(root.GetProperty("page").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("limit").GetInt32(), Is.EqualTo(50));
            Assert.That(root.GetProperty("totalItems").GetInt64(), Is.Zero);
            Assert.That(root.GetProperty("totalPages").GetInt32(), Is.Zero);
        });
    }

    [Test]
    public async Task Index_rows_carry_catalog_fields_when_names_are_in_the_index()
    {
        await SeedAsync("shelly-bin");
        await SeedAsync("a-apple");

        var response = await _client.GetAsync("/v1/packages?limit=2");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");

        Assert.That(items.GetArrayLength(), Is.EqualTo(2));

        var apple = items[0];
        Assert.Multiple(() =>
        {
            Assert.That(apple.GetProperty("name").GetString(), Is.EqualTo("a-apple"));
            Assert.That(apple.GetProperty("description").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(apple.GetProperty("version").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(apple.GetProperty("numVotes").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(apple.GetProperty("popularity").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(apple.GetProperty("outOfDate").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });

        var shelly = items[1];
        Assert.Multiple(() =>
        {
            Assert.That(shelly.GetProperty("name").GetString(), Is.EqualTo("shelly-bin"));
            Assert.That(shelly.GetProperty("description").GetString(),
                Is.EqualTo("Shelly: A Modern Arch Package Manager (prebuilt binary)"));
            Assert.That(shelly.GetProperty("version").GetString(), Is.EqualTo("1.2.3-1"));
            Assert.That(shelly.GetProperty("numVotes").GetInt32(), Is.EqualTo(10));
            Assert.That(shelly.GetProperty("popularity").GetDouble(), Is.EqualTo(0));
            Assert.That(shelly.GetProperty("outOfDate").GetInt64(), Is.EqualTo(1735689600));
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
