using System.Security.Cryptography.X509Certificates;
using Atoll.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AtollOptions>(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<PackageIndexStore>();
builder.Services.AddSingleton<PackageQueryService>();
builder.Services.AddSingleton<PackageRefreshCoordinator>();
builder.Services.AddSingleton(new ApplicationRuntimeInfo(DateTimeOffset.UtcNow));
builder.Services.AddHostedService<PackageRefreshWorker>();

builder.WebHost.ConfigureKestrel((context, kestrel) =>
{
    var options = context.Configuration.Get<AtollOptions>() ?? new AtollOptions();

    if (!string.IsNullOrWhiteSpace(options.Key) && !string.IsNullOrWhiteSpace(options.Cert))
    {
        var certificate = X509Certificate2.CreateFromPemFile(options.Cert, options.Key);

        kestrel.ListenAnyIP(443, listenOptions => listenOptions.UseHttps(certificate));
        return;
    }

    kestrel.ListenAnyIP(options.Port);
});

var app = builder.Build();

app.MapMethods("/health", ["GET", "HEAD"], Health);
app.MapGet("/packages", Packages);
app.MapGet("/metrics", Metrics);

app.MapMethods("/{*path}", ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"], NotFoundHtml)
    .WithOrder(int.MaxValue);

await app.RunAsync();
return;

static IResult Health()
{
    return Results.Ok();
}

static IResult Packages(string? names, string? by, PackageQueryService queryService)
{
    var parsedNames = QueryParsing.ParseNames(names);

    return by switch
    {
        "prov" => Results.Ok(queryService.FindByProvides(parsedNames)),
        "desc" => Results.Ok(queryService.FindByWords(parsedNames)),
        null or "" => Results.Ok(queryService.FindByNames(parsedNames)),
        _ => NotFoundHtml()
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

static IResult NotFoundHtml()
{
    return Results.Content("That route does not exist.", "text/html", null, StatusCodes.Status404NotFound);
}