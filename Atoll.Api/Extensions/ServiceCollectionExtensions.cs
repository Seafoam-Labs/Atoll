using System.IO.Compression;
using System.Text.Json.Serialization;
using Atoll.Api.Services.Metrics;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Atoll.Api.Extensions;

internal static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAtollOptions(IConfiguration configuration)
        {
            services.AddOptions<AtollOptions>()
                .Bind(configuration.GetSection("Atoll"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<SecurityOptions>()
                .Bind(configuration.GetSection("Atoll:Security"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddOptions<UiOptions>()
                .Bind(configuration.GetSection("Atoll:Ui"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return services;
        }

        public IServiceCollection AddAtollInfrastructure()
        {
            services.AddOpenApi();
            services.AddHttpClient();

            services.AddResponseCompression(options =>
            {
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
                [
                    "image/svg+xml",
                    "application/xml"
                ]);

                options.Providers.Add<BrotliCompressionProvider>();
                options.Providers.Add<GzipCompressionProvider>();
            });

            services.Configure<BrotliCompressionProviderOptions>(options => { options.Level = CompressionLevel.Fastest; });

            services.Configure<GzipCompressionProviderOptions>(options => { options.Level = CompressionLevel.Fastest; });

            services.Configure<JsonOptions>(options =>
                options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            services.AddExceptionHandler<GlobalExceptionHandler>();
            services.AddProblemDetails();

            services.AddSingleton<IMongoClient>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
                return new MongoClient(options.Mongo.ConnectionString);
            });

            return services;
        }

        public IServiceCollection AddAtollObservability()
        {
            services.AddSingleton<AtollMetrics>();

            services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.AddService("Atoll.Api", serviceVersion: "1.0.0"))
                .UseOtlpExporter()
                .WithMetrics(metrics => metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddInstrumentation(sp => sp.GetRequiredService<AtollMetrics>())
                    .AddMeter(AtollMetrics.MeterName)
                    .AddPrometheusExporter());

            return services;
        }

        public IServiceCollection AddCatalogServices()
        {
            services.AddSingleton<PackageIndexStore>();
            services.AddSingleton<PackageSearchService>();
            services.AddSingleton<IAurMetadataRepository, AurMetadataRepository>();
            services.AddSingleton<UpstreamPackageReconciler>();
            services.AddSingleton<PackageIndexUpdater>();
            services.AddHostedService<PackageIndexWorker>();
            return services;
        }

        public IServiceCollection AddPackageServices()
        {
            services.AddSingleton<IPackageRepository, MongoPackageRepository>();
            services.AddSingleton<ISeedExclusionRepository, MongoSeedExclusionRepository>();
            services.AddSingleton<IAurPackageSource, AurGitPackageSource>();
            services.AddSingleton<DirectPackageSeeder>();
            services.AddSingleton<IPackageService, PackageService>();
            return services;
        }

        public IServiceCollection AddGitServices()
        {
            services.AddSingleton<IGitRepositoryCache, GitRepositoryCache>();
            services.AddSingleton<IGitTransferService, GitTransferService>();
            return services;
        }

        public IServiceCollection AddSecurityServices(IConfiguration configuration)
        {
            var securityEnabled = configuration.GetSection("Atoll:Security").Get<SecurityOptions>()?.Enabled ?? true;

            services.AddSingleton<IPackageSecurityScanner, PkgBuildSecurityScanner>();
            services.AddSingleton<IPackageSecurityRepository, MongoPackageSecurityRepository>();
            services.AddSingleton<IPackageSecurityAccess, PackageSecurityAccess>();
            services.AddSingleton<PackageSecurityFilter>();
            services.AddSingleton(new SecurityScanStatusStore(securityEnabled));
            services.AddHostedService<PackageSecurityWorker>();
            return services;
        }

        /// <summary>
        ///     Seed-mode, refresh, and mirror selection are deployment policy: exactly one direct/bulk
        ///     seed worker or none for Off, refresh independent of seed mode, one shared mirror when
        ///     bulk seed or refresh needs it, and status stores registered even when disabled.
        /// </summary>
        public IServiceCollection AddSyncServices(IConfiguration configuration)
        {
            var seedMode = configuration.GetSection("Atoll:Seed").Get<SeedOptions>()?.Mode ?? SeedMode.Direct;
            var bulkEnabled = seedMode == SeedMode.Bulk;
            var refreshEnabled = configuration.GetSection("Atoll:Refresh").Get<RefreshOptions>()?.Enabled ?? false;

            services.AddSingleton(new BulkSeedStatusStore(bulkEnabled));
            services.AddSingleton(new DirectSeedStatusStore(seedMode == SeedMode.Direct));
            services.AddSingleton(new RefreshStatusStore(refreshEnabled));

            if (bulkEnabled || refreshEnabled)
                services.AddSingleton<IAurMirror>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
                    var (mirrorUrl, cachePath) = bulkEnabled
                        ? (options.Seed.Bulk.MirrorUrl, options.Seed.Bulk.CachePath)
                        : (options.Refresh.MirrorUrl, options.Refresh.CachePath);
                    var logger = sp.GetRequiredService<ILogger<AurMirror>>();
                    return new AurMirror(mirrorUrl, cachePath, logger);
                });

            switch (seedMode)
            {
                case SeedMode.Bulk:
                    services.AddHostedService<PackageBulkSeedWorker>();
                    break;
                case SeedMode.Direct:
                    services.AddHostedService<DirectSeedWorker>();
                    break;
                case SeedMode.Off:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported seed mode: {seedMode}.");
            }

            if (refreshEnabled)
                services.AddHostedService<PackageRefreshWorker>();

            return services;
        }

        public IServiceCollection AddUiServices()
        {
            services.AddSingleton<PackageCatalogService>();
            services.AddSingleton<PackageDetailsService>();
            services.AddSingleton<StatusDashboardService>();

            services.AddHttpContextAccessor();
            services.AddAntiforgery();
            services.AddRazorComponents()
                .AddInteractiveServerComponents();

            return services;
        }
    }
}