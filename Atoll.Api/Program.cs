using System.Text.Json.Serialization;
using Atoll.Api;
using Atoll.Api.Components;
using Atoll.Api.Services.Metrics;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Packages.Mirror;
using Atoll.Api.Services.Packages.Refresh;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Ui;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddOptions<AtollOptions>()
    .Bind(builder.Configuration.GetSection("Atoll"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection("Atoll:Security"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<UiOptions>()
    .Bind(builder.Configuration.GetSection("Atoll:Ui"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.Configure<JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddSingleton<PackageIndexStore>();
builder.Services.AddSingleton<PackageSearchService>();
builder.Services.AddSingleton<IAurMetadataRepository, AurMetadataRepository>();
builder.Services.AddSingleton<PackageIndexUpdater>();
builder.Services.AddSingleton<AtollMetrics>();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Atoll.Api", serviceVersion: "1.0.0"))
    .UseOtlpExporter()
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddInstrumentation(sp => sp.GetRequiredService<AtollMetrics>())
        .AddMeter(AtollMetrics.MeterName)
        .AddPrometheusExporter());

builder.Logging.AddOpenTelemetry();

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
    return new MongoClient(options.Mongo.ConnectionString);
});

builder.Services.AddSingleton<IPackageRepository, MongoPackageRepository>();
builder.Services.AddSingleton<ISeedExclusionRepository, MongoSeedExclusionRepository>();
builder.Services.AddSingleton<IPackageService, MongoPackageService>();
builder.Services.AddSingleton<IGitTransferService, GitTransferService>();

builder.Services.AddSingleton<IPackageSecurityScanner, PkgBuildSecurityScanner>();
builder.Services.AddSingleton<IPackageSecurityRepository, MongoPackageSecurityRepository>();
builder.Services.AddSingleton<IPackageSecurityAccess, PackageSecurityAccess>();
builder.Services.AddSingleton<PackageSecurityFilter>();
builder.Services.AddHostedService<PackageSecurityWorker>();

builder.Services.AddSingleton<PackageCatalogService>();
builder.Services.AddSingleton<PackageDetailsService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddAntiforgery();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var seedMode = builder.Configuration.GetSection("Atoll:Seed").Get<SeedOptions>()?.Mode ?? SeedMode.Direct;
var bulkEnabled = seedMode == SeedMode.Bulk;
var refreshEnabled = builder.Configuration.GetSection("Atoll:Refresh").Get<RefreshOptions>()?.Enabled ?? false;
var securityEnabled = builder.Configuration.GetSection("Atoll:Security").Get<SecurityOptions>()?.Enabled ?? true;

builder.Services.AddSingleton(new BulkSeedStatusStore(bulkEnabled));
builder.Services.AddSingleton(new DirectSeedStatusStore(seedMode == SeedMode.Direct));
builder.Services.AddSingleton(new RefreshStatusStore(refreshEnabled));
builder.Services.AddSingleton(new SecurityScanStatusStore(securityEnabled));
builder.Services.AddSingleton<StatusDashboardService>();

if (bulkEnabled || refreshEnabled)
    builder.Services.AddSingleton<IAurMirror>(sp =>
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
        builder.Services.AddHostedService<PackageBulkSeedWorker>();
        break;
    case SeedMode.Direct:
        builder.Services.AddHostedService<DirectSeedWorker>();
        break;
    case SeedMode.Off:
        break;
    default:
        throw new InvalidOperationException($"Unsupported seed mode: {seedMode}.");
}

if (refreshEnabled)
    builder.Services.AddHostedService<PackageRefreshWorker>();

builder.Services.AddHostedService<PackageIndexWorker>();

var app = builder.Build();

app.UseExceptionHandler();

// Re-execute 4xx/5xx responses with empty bodies as /not-found so browsers get
// the styled page - the Blazor fallback alone renders nothing for unmatched paths.
// Only GET/HEAD requests are re-executed; non-GET methods (e.g. POST git-upload-pack)
// must preserve their status code without routing into Blazor's GET-only /not-found.
app.UseWhen(
    context => HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));

app.UseStaticFiles();
app.MapStaticAssets();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseRouting();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapEndpoints();
app.MapPrometheusScrapingEndpoint();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();