using NUnit.Framework;
using Testcontainers.MongoDb;

// Kept in the root namespace (not Support) because NUnit [SetUpFixture] only applies to the
// containing namespace and its descendants, and the Mongo tests span Search/, Packages/, Endpoints/.
// ReSharper disable once CheckNamespace
namespace Atoll.Api.Tests;

[SetUpFixture]
public sealed class MongoFixture
{
    private static MongoDbContainer? Container { get; set; }

    public static string? UnavailableReason { get; private set; }

    public static bool IsAvailable => Container is not null;

    public static string ConnectionString =>
        Container?.GetConnectionString() ?? throw new InvalidOperationException("Mongo container is unavailable.");

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
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

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (Container is not null)
            await Container.DisposeAsync();
    }
}