using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Support;

internal static class MongoRepositoryFactory
{
    public static IMongoClient CreateClient()
    {
        return new MongoClient(MongoFixture.ConnectionString);
    }

    private static IOptions<AtollOptions> CreateOptions(string database)
    {
        return Options.Create(new AtollOptions
        {
            Mongo = new MongoOptions
            {
                ConnectionString = MongoFixture.ConnectionString,
                Database = database,
                MaxRevisions = 10,
                MaxFileBytes = 5_242_880
            }
        });
    }

    public static MongoPackageRepository CreatePackageRepository(IMongoClient client, string database)
    {
        return new MongoPackageRepository(client, CreateOptions(database));
    }

    public static MongoAurMetadataRepository CreateAurMetadataRepository(IMongoClient client, string database)
    {
        return new MongoAurMetadataRepository(client, CreateOptions(database));
    }

    public static async Task DropDatabaseAsync(IMongoClient client, string database)
    {
        await client.DropDatabaseAsync(database);
    }

    public static string NewDatabaseName()
    {
        return $"atoll-test-{Guid.NewGuid():N}";
    }
}