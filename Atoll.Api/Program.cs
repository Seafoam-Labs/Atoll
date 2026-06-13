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

app.MapMethods("/health", ["GET", "HEAD"], () => Results.Ok());
app.MapGet("/packages", Packages);
app.MapGet("/metrics", Metrics);

app.MapFallback("/{**path}", ([FromRoute] string? path) => Results.NotFound());

await app.RunAsync();
return;

static IResult Packages(
    [FromServices] PackageQueryService queryService,
    [FromQuery(Name = "names")] SearchTerms? names,
    [FromQuery(Name = "by")] QueryType? by = QueryType.Name)
{
    var queryNames = names?.Values.ToHashSet() ?? [];

    return by switch
    {
        QueryType.Name => Results.Ok(queryService.FindByNames(queryNames)),
        QueryType.Desc => Results.Ok(queryService.FindByWords(queryNames)),
        QueryType.Prov => Results.Ok(queryService.FindByProvides(queryNames)),
        _ => throw new ArgumentOutOfRangeException(nameof(by), by, null)
    };
}

static IResult Metrics([FromServices] MetricsService metricsService)
{
    return Results.Ok(metricsService.GetMetrics());
}