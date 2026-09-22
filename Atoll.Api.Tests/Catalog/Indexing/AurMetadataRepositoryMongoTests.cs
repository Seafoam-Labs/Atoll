using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Tests.Support;
using MongoDB.Driver;
using Xunit;

namespace Atoll.Api.Tests.Catalog.Indexing;

[Trait("Category", "RequiresMongo")]
public sealed class AurMetadataRepositoryMongoTests : AurMetadataRepositoryContract, IAsyncLifetime
{
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
}