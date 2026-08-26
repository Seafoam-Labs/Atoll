using Atoll.Api.Services.Packages;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Atoll.Api.Services.Packages.Persistence;

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
                ["Atoll:Mongo:Database"] = Database,
                // These tests check Mongo storage mechanics, not security.
                ["Atoll:Security:Enabled"] = "false"
            })
            .Build());

        builder.ConfigureServices(services => { services.RemoveAll<IHostedService>(); });
    }

    public IPackageRepository CreatePackageRepository()
    {
        return MongoRepositoryFactory.CreatePackageRepository(
            MongoRepositoryFactory.CreateClient(),
            Database);
    }
}