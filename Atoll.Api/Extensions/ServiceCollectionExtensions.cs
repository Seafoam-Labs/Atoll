using System.IO.Compression;
using System.Net;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Atoll.Api.Services.Metrics;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages.Persistence;
using Atoll.Api.Services.Sync.Bulk;
using Atoll.Api.Services.Sync.Direct;
using Atoll.Api.Services.Sync.Mirror;
using Atoll.Api.Services.Sync.Refresh;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Rpc;
using Atoll.Api.Services.Catalog.Indexing;
using Atoll.Api.Services.Catalog.Persistence;
using Atoll.Api.Services.Catalog.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Security.Persistence;
using Atoll.Api.Services.Ui;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using IPNetwork = System.Net.IPNetwork;
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

            services.AddOptions<ProxyOptions>()
                .Bind(configuration.GetSection("Atoll:Proxy"))
                .Validate(options =>
                    SplitProxyList(options.KnownNetworks).All(network => TryParseStrictCidr(network, out _)) &&
                    SplitProxyList(options.KnownProxies).All(proxy => IPAddress.TryParse(proxy, out _)) &&
                    options.ForwardLimit is null or >= 1,
                    "Atoll:Proxy entries must be comma-separated CIDR networks (e.g. 172.31.0.0/16 with host bits zero) and IP addresses, with a ForwardLimit of at least 1.")
                .ValidateOnStart();

            return services;
        }

        public IServiceCollection AddAtollInfrastructure()
        {
            services.AddApiVersioning(options =>
                {
                    options.ApiVersionReader = new UrlSegmentApiVersionReader();
                    options.ReportApiVersions = true;
                })
                .AddApiExplorer(options =>
                {
                    options.GroupNameFormat = "'v'VVV";
                    options.SubstituteApiVersionInUrl = true;
                })
                .AddOpenApi();

            services.AddHttpClient();

            // Restore the original client IP and scheme from trusted proxies.
            // Without configuration, the framework trusts loopback only.
            services.AddOptions<ForwardedHeadersOptions>()
                .Configure<IOptions<ProxyOptions>>((forwarded, proxy) =>
                {
                    forwarded.ForwardedHeaders =
                        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                    if (!string.IsNullOrWhiteSpace(proxy.Value.ForwardedProtoHeaderName))
                        forwarded.ForwardedProtoHeaderName = proxy.Value.ForwardedProtoHeaderName;

                    if (proxy.Value.ForwardLimit is { } forwardLimit)
                        forwarded.ForwardLimit = forwardLimit;

                    var knownNetworks = SplitProxyList(proxy.Value.KnownNetworks);
                    var knownProxies = SplitProxyList(proxy.Value.KnownProxies);

                    if (knownNetworks.Length == 0 && knownProxies.Length == 0)
                        return;

                    // Replace the loopback defaults with the configured trust list.
                    forwarded.KnownIPNetworks.Clear();
                    forwarded.KnownProxies.Clear();

                    foreach (var network in knownNetworks)
                    {
                        if (!TryParseStrictCidr(network, out var ipNetwork))
                            throw new InvalidOperationException(
                                $"Atoll:Proxy:KnownNetworks entry '{network}' is not strict CIDR notation (expected e.g. 172.31.0.0/16 with the host bits set to zero).");

                        forwarded.KnownIPNetworks.Add(ipNetwork);
                    }

                    foreach (var proxyAddress in knownProxies)
                        forwarded.KnownProxies.Add(IPAddress.Parse(proxyAddress));
                });

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
            services.AddSingleton<AurRpcService>();
            services.AddSingleton<IAurMetadataRepository, MongoAurMetadataRepository>();
            services.AddSingleton<AurMetadataClient>();
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
            services.AddSingleton<PackageSecurityStatusService>();
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

    private static string[] SplitProxyList(string? value) =>
        value?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>
    ///     IPNetwork.TryParse silently masks off host bits ("172.31.0.1/16"
    ///     becomes 172.31.0.0/16), which would quietly widen trust. Require the
    ///     network address itself so typos fail at startup instead.
    /// </summary>
    private static bool TryParseStrictCidr(string? network, out IPNetwork ipNetwork)
    {
        if (network is not null &&
            IPNetwork.TryParse(network, out ipNetwork) &&
            IPAddress.TryParse(network[..network.LastIndexOf('/')], out var supplied) &&
            ipNetwork.BaseAddress.Equals(supplied))
            return true;

        ipNetwork = default;
        return false;
    }
}