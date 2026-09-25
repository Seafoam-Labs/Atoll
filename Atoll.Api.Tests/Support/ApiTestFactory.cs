using Atoll.Api.Services.Catalog.Indexing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Atoll.Api.Tests.Support;

internal sealed class ApiTestFactory : WebApplicationFactory<Program>
{
    /// <summary>Optional replacement for the three-package sample index, for corpus-shape tests.</summary>
    internal SearchIndexData? Index { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<PackageIndexStore>();
            services.UseInMemoryRepositories();

            var store = new PackageIndexStore();
            store.Replace(Index ?? TestData.LoadSampleIndexes());

            services.AddSingleton(store);
        });
    }
}