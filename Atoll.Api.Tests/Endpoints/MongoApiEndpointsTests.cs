using System.Net;
using System.Text.Json;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using NUnit.Framework;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Endpoints;

[Category("RequiresMongo")]
public class MongoApiEndpointsTests
{
    private HttpClient _client = null!;
    private MongoApiTestFactory _factory = null!;
    private IMongoClient _mongo = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(
            MongoFixture.IsAvailable,
            Is.True,
            $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _factory = new MongoApiTestFactory();
        _client = _factory.CreateClient();
        _mongo = MongoRepositoryFactory.CreateClient();
    }

    [TearDown]
    public async Task TearDown()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await MongoRepositoryFactory.DropDatabaseAsync(_mongo, _factory.Database);
    }

    [Test]
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

        Assert.That(list.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(versions.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var listBody = await list.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(listBody);
        Assert.That(doc.RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(doc.RootElement[0].GetString(), Is.EqualTo("atoll-test"));
    }

    [Test]
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
        Assert.That(del.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        Assert.That(await repo.ExistsAsync("to-delete"), Is.False);

        // Cascade: the deleted package's revision content documents are also gone.
        var revisionDocs = _mongo.GetDatabase(_factory.Database).GetCollection<BsonDocument>("package-revisions");
        Assert.That(
            await revisionDocs.CountDocumentsAsync(new BsonDocument("packageName", "to-delete")),
            Is.EqualTo(0));

        // Cascade: the deleted package's security scan documents are also gone.
        Assert.That(
            await scans.CountDocumentsAsync(new BsonDocument("packageName", "to-delete")),
            Is.EqualTo(0));
    }
}