using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Endpoints;

[Trait("Category", "RequiresMongo")]
public class MongoApiEndpointsTests : IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly MongoApiTestFactory _factory;
    private readonly IMongoClient _mongo;

    public MongoApiEndpointsTests()
    {
        Assert.SkipUnless(
            MongoFixture.IsAvailable,
            $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _factory = new MongoApiTestFactory();
        _client = _factory.CreateClient();
        _mongo = MongoRepositoryFactory.CreateClient();
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await MongoRepositoryFactory.DropDatabaseAsync(_mongo, _factory.Database);
    }

    [Fact]
    public async Task SeededPackageIsServedFromRealMongoStorage()
    {
        var repo = _factory.CreatePackageRepository();
        var now = DateTimeOffset.UtcNow;
        var revisionContent = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("atoll-test", "rev-1"),
            PackageName = "atoll-test",
            RevisionId = "rev-1",
            CreatedAt = now,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=atoll-test\n", Size = 18, Hash = "h" }
            }
        };
        await repo.InsertSeedAsync(new PackageDocument
        {
            Id = "atoll-test",
            PackageName = "atoll-test",
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = "rev-1",
            Revisions =
            [
                new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = now, Author = "test", Message = "seed" }
            ]
        }, revisionContent);

        var list = await _client.GetAsync("/v1/packages");
        var head = await _client.GetAsync("/v1/packages/atoll-test");
        var versions = await _client.GetAsync("/v1/packages/atoll-test/versions");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HttpStatusCode.OK, versions.StatusCode);

        var listBody = await list.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(listBody);
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("atoll-test", items[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task PackageIndexIsServedPagedFromRealMongoStorage()
    {
        var repo = _factory.CreatePackageRepository();
        var now = DateTimeOffset.UtcNow;

        foreach (var name in new[] { "zulu", "alpha", "mike" })
        {
            await repo.InsertSeedAsync(new PackageDocument
            {
                Id = name,
                PackageName = name,
                CreatedAt = now,
                UpdatedAt = now,
                HeadRevisionId = "rev-1",
                Revisions =
                [
                    new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = now, Author = "test", Message = "seed" }
                ]
            }, new PackageRevisionContentDocument
            {
                Id = PackageSchema.RevisionDocumentId(name, "rev-1"),
                PackageName = name,
                RevisionId = "rev-1",
                CreatedAt = now,
                Author = "test",
                Message = "seed",
                Files = new Dictionary<string, PackageFile>
                {
                    ["PKGBUILD"] = new() { Content = $"pkgname={name}\n", Size = 8 + name.Length, Hash = "h" }
                }
            });
        }

        var response = await _client.GetAsync("/v1/packages?limit=2&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Multiple(() =>
        {
            Assert.Equal(2, root.GetProperty("page").GetInt32());
            Assert.Equal(2, root.GetProperty("limit").GetInt32());
            Assert.Equal(3, root.GetProperty("totalItems").GetInt64());
            Assert.Equal(2, root.GetProperty("totalPages").GetInt32());

            var items = root.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            Assert.Equal("zulu", items[0].GetProperty("name").GetString());
            Assert.Equal("rev-1", items[0].GetProperty("headRevisionId").GetString());
            Assert.Equal(1, items[0].GetProperty("revisionCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, items[0].GetProperty("upstreamPackageBase").ValueKind);
        });
    }

    [Fact]
    public async Task DeletePackagePersistsToRealMongo()
    {
        var repo = _factory.CreatePackageRepository();
        var now = DateTimeOffset.UtcNow;
        var revisionContent = new PackageRevisionContentDocument
        {
            Id = PackageSchema.RevisionDocumentId("to-delete", "rev-1"),
            PackageName = "to-delete",
            RevisionId = "rev-1",
            CreatedAt = now,
            Author = "test",
            Message = "seed",
            Files = new Dictionary<string, PackageFile>
            {
                ["PKGBUILD"] = new() { Content = "pkgname=to-delete\n", Size = 18, Hash = "h" }
            }
        };
        await repo.InsertSeedAsync(new PackageDocument
        {
            Id = "to-delete",
            PackageName = "to-delete",
            CreatedAt = now,
            UpdatedAt = now,
            HeadRevisionId = "rev-1",
            Revisions =
            [
                new PackageRevisionDocument { RevisionId = "rev-1", CreatedAt = now, Author = "test", Message = "seed" }
            ]
        }, revisionContent);

        // A head scan record exists before the delete; the cascade must remove it too.
        var scans = _mongo.GetDatabase(_factory.Database).GetCollection<BsonDocument>("package-security-scans");
        await scans.InsertOneAsync(new BsonDocument
        {
            ["_id"] = "to-delete:rev-1",
            ["packageName"] = "to-delete",
            ["revisionId"] = "rev-1",
            ["isHead"] = true,
            ["status"] = "Pending"
        });

        var del = await _client.DeleteAsync("/v1/packages/to-delete");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        Assert.False(await repo.ExistsAsync("to-delete"));

        // Cascade: the deleted package's revision content documents are also gone.
        var revisionDocs = _mongo.GetDatabase(_factory.Database).GetCollection<BsonDocument>("package-revisions");
        Assert.Equal(
            0,
            await revisionDocs.CountDocumentsAsync(new BsonDocument("packageName", "to-delete")));

        // Cascade: the deleted package's security scan documents are also gone.
        Assert.Equal(
            0,
            await scans.CountDocumentsAsync(new BsonDocument("packageName", "to-delete")));
    }
}