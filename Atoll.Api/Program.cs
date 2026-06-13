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

IResult Packages([AsParameters] PackagesQuery query, PackageQueryService queryService)
{
    var names = query.Names?.Parts.ToHashSet() ?? [];

    return query.By switch
    {
        "prov" => Results.Ok(queryService.FindByProvides(names)),
        "desc" => Results.Ok(queryService.FindByWords(names)),
        null or "" => Results.Ok(queryService.FindByNames(names)),
        _ => Results.NotFound()
    };
}

static IResult Metrics(
    PackageIndexStore store,
    PackageQueryService queryService,
    PackageRefreshCoordinator refreshCoordinator,
    ApplicationRuntimeInfo runtimeInfo)
{
    var snapshot = store.Current;
    var refresh = refreshCoordinator.GetStatus();

    var response = new MetricsResponse
    {
        UptimeSeconds = (long)(DateTimeOffset.UtcNow - runtimeInfo.StartedAtUtc).TotalSeconds,
        RequestCount = queryService.RequestCount,
        IndexSizes = new IndexSizes
        {
            ByNames = snapshot.ByNames.Count,
            ByProvides = snapshot.ByProvides.Count,
            ByWords = snapshot.ByWords.Count
        },
        Refresh = new RefreshStatus
        {
            DataFile = refresh.DataFile,
            IntervalSeconds = (long)refresh.Interval.TotalSeconds,
            Attempts = refresh.Attempts,
            Successes = refresh.Successes,
            Failures = refresh.Failures,
            LastStartedUtc = refresh.LastStartedUtc,
            LastSucceededUtc = refresh.LastSucceededUtc,
            LastFailedUtc = refresh.LastFailedUtc
        }
    };

    return Results.Ok(response);
}