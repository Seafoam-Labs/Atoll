using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Tests.Support;

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
                ["Atoll:Git:RepositoriesPath"] = RepositoriesRoot,
                // These tests check Git transfer mechanics, not security.
                ["Atoll:Security:Enabled"] = "false"
            })
            .Build());

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<PackageIndexStore>();
            services.RemoveAll<IPackageRepository>();
            services.RemoveAll<IPackageService>();
            services.RemoveAll<IPackageSecurityRepository>();
            services.RemoveAll<IAurMetadataRepository>();

            var store = new PackageIndexStore();
            store.Replace(TestData.LoadSampleIndexesAsync().GetAwaiter().GetResult());
            services.AddSingleton(store);

            services.AddSingleton<IPackageRepository>(Repository);
            services.AddSingleton<IPackageSecurityRepository>(new InMemoryPackageSecurityRepository());
            services.AddSingleton<IAurMetadataRepository>(_ => new InMemoryAurMetadataRepository());
            services.AddSingleton<IPackageService, PackageService>();
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
                // ignore
            }

        base.Dispose(disposing);
    }
}