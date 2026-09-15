using Testcontainers.MongoDb;
using Xunit;

// Root namespace (not Support) because the Mongo tests span Packages/, Security/, Catalog/, and Endpoints/
// and reach this fixture unqualified.
// ReSharper disable once CheckNamespace
namespace Atoll.Api.Tests;

public sealed class MongoFixture : IAsyncLifetime
{
    private static MongoDbContainer? Container { get; set; }

    public static string? UnavailableReason { get; private set; }

    public static bool IsAvailable => Container is not null;

    public static string ConnectionString =>
        Container?.GetConnectionString() ?? throw new InvalidOperationException("Mongo container is unavailable.");

    public async ValueTask InitializeAsync()
    {
        try
        {
            Container = new MongoDbBuilder("mongo:8.3.7")
                .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1") // https://jira.mongodb.org/browse/SERVER-121912
                .Build();

            await Container.StartAsync();
        }
        catch (Exception ex)
        {
            Container = null;
            UnavailableReason = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
            await Container.DisposeAsync();
    }
}