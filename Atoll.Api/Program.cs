using Atoll.Api;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.Configure<AtollOptions>(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PackageIndexStore>();
builder.Services.AddSingleton<PackageQueryService>();
builder.Services.AddSingleton<PackageRefreshCoordinator>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton(new ApplicationRuntimeInfo(DateTimeOffset.UtcNow));
builder.Services.AddHostedService<PackageRefreshWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapMethods("/health", ["GET", "HEAD"], TypedResults.Ok);
app.MapGet("/packages", Packages);
app.MapGet("/metrics", Metrics);

app.MapFallback("/{**path}", ([FromRoute] string? path) => TypedResults.NotFound());

await app.RunAsync();
return;

static IResult Packages(
    [FromServices] PackageQueryService queryService,
    [FromQuery(Name = "query")] QueryValues? query,
    [FromQuery(Name = "by")] QueryType? by = QueryType.Name)
{
    var queryValues = query?.Values.ToHashSet() ?? [];

    return by switch
    {
        QueryType.Name => TypedResults.Ok(queryService.FindByNames(queryValues)),
        QueryType.Desc => TypedResults.Ok(queryService.FindByWords(queryValues)),
        QueryType.Prov => TypedResults.Ok(queryService.FindByProvides(queryValues)),
        _ => throw new ArgumentOutOfRangeException(nameof(by), by, null)
    };
}

static IResult Metrics([FromServices] MetricsService metricsService)
{
    return TypedResults.Ok(metricsService.GetMetrics());
}