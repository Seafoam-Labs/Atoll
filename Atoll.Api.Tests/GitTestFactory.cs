using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Atoll.Api.Tests;

internal sealed class GitTestFactory : WebApplicationFactory<Program>
{
    private InMemoryPackageRepository Repository { get; } = new();

    private string RepositoriesRoot { get; } = Path.Combine(Path.GetTempPath(), $"atoll-http-git-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atoll:Git:RepositoriesPath"] = RepositoriesRoot
            })
            .Build());

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<PackageIndexStore>();
            services.RemoveAll<IPackageRepository>();
            services.RemoveAll<IPackageService>();

            var store = new PackageIndexStore();
            store.Replace(TestData.LoadSampleIndexesAsync().GetAwaiter().GetResult());
            services.AddSingleton(store);

            services.AddSingleton<IPackageRepository>(Repository);
            services.AddSingleton<IPackageService, MongoPackageService>();
            services.AddSingleton<IGitTransferService, GitTransferService>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            try
            {
                if (Directory.Exists(RepositoriesRoot))
                    Directory.Delete(RepositoriesRoot, true);
            }
            catch
            {
                // best-effort cleanup
            }

        base.Dispose(disposing);
    }
}