using Atoll.Api.Services.Packages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Atoll.Api.Tests.Support;

internal sealed class MongoApiTestFactory : WebApplicationFactory<Program>
{
    public string Database { get; } = MongoRepositoryFactory.NewDatabaseName();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atoll:Mongo:ConnectionString"] = MongoFixture.ConnectionString,
                ["Atoll:Mongo:Database"] = Database
            })
            .Build());

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();

            // Keep IPackageRepository / IAurMetadataRepository / IMongoClient as registered in
            // Program.cs so the host exercises real Mongo-backed storage.
        });
    }

    public IPackageRepository CreatePackageRepository()
    {
        return MongoRepositoryFactory.CreatePackageRepository(
            MongoRepositoryFactory.CreateClient(),
            Database);
    }
}