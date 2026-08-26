using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Security;
using Atoll.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Atoll.Api.Tests.Support;

internal sealed class SecurityTestFactory : WebApplicationFactory<Program>
{
    public InMemoryPackageRepository Repository { get; } = new();
    public InMemoryPackageSecurityRepository SecurityRepository { get; } = new();
    public InMemorySeedExclusionRepository SeedExclusions { get; } = new();

    /// <summary>Set false to render the /status security card in its bypassed state.</summary>
    public bool SecurityEnabled { get; init; } = true;

    /// <summary>Set false to render /status with an empty (not-yet-loaded) index.</summary>
    public bool LoadSampleIndex { get; init; } = true;

    /// <summary>Set false to gate the manual seed/rescan mutations (REST 403 + hidden UI buttons).</summary>
    public bool MutationsEnabled { get; init; } = true;

    /// <summary>Optional override for the public base URL rendered in UI clone blocks.</summary>
    public string? ExternalBaseUrl { get; init; }

    private string RepositoriesRoot { get; } = Path.Combine(Path.GetTempPath(), $"atoll-security-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        var config = new Dictionary<string, string?>
        {
            ["Atoll:Git:RepositoriesPath"] = RepositoriesRoot,
            ["Atoll:Security:Enabled"] = SecurityEnabled ? "true" : "false",
            ["Atoll:Mutations:Enabled"] = MutationsEnabled ? "true" : "false",
            // Deterministic worker cards on /status regardless of appsettings.json defaults.
            ["Atoll:Seed:Mode"] = "Direct",
            ["Atoll:Refresh:Enabled"] = "false"
        };
        if (ExternalBaseUrl is not null)
            config["Atoll:Ui:ExternalBaseUrl"] = ExternalBaseUrl;

        builder.UseConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build());

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<PackageIndexStore>();
            services.RemoveAll<IPackageRepository>();
            services.RemoveAll<IPackageService>();
            services.RemoveAll<IPackageSecurityRepository>();
            services.RemoveAll<IAurMetadataRepository>();
            services.RemoveAll<ISeedExclusionRepository>();

            var store = new PackageIndexStore();
            if (LoadSampleIndex)
                store.Replace(TestData.LoadSampleIndexesAsync().GetAwaiter().GetResult());
            services.AddSingleton(store);

            services.AddSingleton<IPackageRepository>(Repository);
            services.AddSingleton<IPackageSecurityRepository>(SecurityRepository);
            services.AddSingleton<ISeedExclusionRepository>(SeedExclusions);
            services.AddSingleton<IAurMetadataRepository>(_ => new InMemoryAurMetadataRepository());
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
                // ignore
            }

        base.Dispose(disposing);
    }
}
