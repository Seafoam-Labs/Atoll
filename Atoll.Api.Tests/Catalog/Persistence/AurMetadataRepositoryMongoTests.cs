using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Persistence;

[Trait("Category", "RequiresMongo")]
public sealed class AurMetadataRepositoryMongoTests : AurMetadataRepositoryContract, IAsyncLifetime
{
    private const string CollectionName = "aur-metadata";
    private const string PointerCollectionName = $"{CollectionName}.pointer";
    private const string LegacyBatchIndexName = "batch_1_aur_id_1";

    private readonly IMongoClient _client;
    private readonly string _database;

    public AurMetadataRepositoryMongoTests()
    {
        Assert.SkipUnless(MongoFixture.IsAvailable, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
    }

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    private protected override IAurMetadataRepository CreateRepository()
    {
        return MongoRepositoryFactory.CreateAurMetadataRepository(_client, _database);
    }

    [Fact]
    public async Task MongoAurMetadataRepository_LegacyBatchLayout_IsDroppedOnConstruction()
    {
        await SeedLegacyBatchLayoutAsync();
        Assert.Contains(CollectionName, await CollectionNamesAsync(), StringComparer.Ordinal);

        var repo = CreateRepository();

        var collections = await CollectionNamesAsync();
        var count = await repo.CountAsync(CancellationToken.None);
        var loaded = await repo.LoadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.DoesNotContain(CollectionName, collections, StringComparer.Ordinal);
            Assert.DoesNotContain(PointerCollectionName, collections, StringComparer.Ordinal);
            Assert.Equal(0, count);
            Assert.Empty(loaded);
        });
    }

    [Fact]
    public async Task MongoAurMetadataRepository_SecondConstruction_DoesNotRecreateLegacyIndex()
    {
        await SeedLegacyBatchLayoutAsync();
        CreateRepository();

        // A second construction must be a no-op rather than a crash on the absent legacy index.
        CreateRepository();

        Assert.DoesNotContain(LegacyBatchIndexName, await IndexNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_EmptyDelta_CreatesNoCollection()
    {
        var repo = CreateRepository();

        await repo.SyncAsync(new AurMetadataDelta([], [], 0), CancellationToken.None);

        Assert.DoesNotContain(CollectionName, await CollectionNamesAsync(), StringComparer.Ordinal);
    }

    [Fact]
    public async Task SyncAsync_KeysDocumentsByName_LeavesOnlyTheIdIndex()
    {
        var repo = CreateRepository();

        await repo.SyncAsync(FullSync(SamplePackages()), CancellationToken.None);

        var indexNames = await IndexNamesAsync();
        var docs = await RawDocumentsAsync();

        Assert.Multiple(() =>
        {
            // Guards the per-write cost: a secondary index here would be maintained on every upsert.
            Assert.Equal(["_id_"], indexNames, StringComparer.Ordinal);
            Assert.NotEmpty(docs);
            Assert.All(docs, doc =>
            {
                Assert.Equal(BsonType.String, doc["_id"].BsonType);
                Assert.Equal(doc["name"].AsString, doc["_id"].AsString);
                Assert.False(doc.Contains("batch"));
            });
        });
    }

    private async Task SeedLegacyBatchLayoutAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = _client.GetDatabase(_database);
        var legacy = db.GetCollection<BsonDocument>(CollectionName);

        await legacy.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                new BsonDocument { ["batch"] = 1, ["aur_id"] = 1 },
                new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);

        await legacy.InsertOneAsync(
            new BsonDocument
            {
                ["batch"] = "deadbeef",
                ["aur_id"] = 1L,
                ["name"] = "legacy-pkg",
                ["package_base"] = "legacy-pkg",
                ["version"] = "1.0-1"
            },
            cancellationToken: ct);

        await db.GetCollection<BsonDocument>(PointerCollectionName)
            .InsertOneAsync(new BsonDocument { ["activeBatchId"] = "deadbeef" }, cancellationToken: ct);
    }

    private async Task<List<string>> CollectionNamesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cursor = await _client.GetDatabase(_database).ListCollectionNamesAsync(cancellationToken: ct);

        return [.. await cursor.ToListAsync(ct)];
    }

    private async Task<List<string>> IndexNamesAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cursor = await _client.GetDatabase(_database)
            .GetCollection<BsonDocument>(CollectionName)
            .Indexes.ListAsync(ct);

        return [.. (await cursor.ToListAsync(ct)).Select(ix => ix["name"].AsString)];
    }

    private async Task<List<BsonDocument>> RawDocumentsAsync()
    {
        return await _client.GetDatabase(_database)
            .GetCollection<BsonDocument>(CollectionName)
            .Find(FilterDefinition<BsonDocument>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
