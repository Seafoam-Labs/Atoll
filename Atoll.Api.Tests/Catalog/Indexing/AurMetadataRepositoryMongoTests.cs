using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Driver;
using NUnit.Framework;

namespace Atoll.Api.Tests.Catalog.Indexing;

[Category("RequiresMongo")]
public class AurMetadataRepositoryMongoTests : AurMetadataRepositoryContract
{
    private IMongoClient _client = null!;
    private string _database = null!;

    [SetUp]
    public void SetUp()
    {
        Assume.That(MongoFixture.IsAvailable, Is.True, $"Mongo unavailable: {MongoFixture.UnavailableReason}");

        _client = MongoRepositoryFactory.CreateClient();
        _database = MongoRepositoryFactory.NewDatabaseName();
    }

    [TearDown]
    public async Task TearDown()
    {
        await MongoRepositoryFactory.DropDatabaseAsync(_client, _database);
    }

    private protected override IAurMetadataRepository CreateRepository()
    {
        return MongoRepositoryFactory.CreateAurMetadataRepository(_client, _database);
    }
}