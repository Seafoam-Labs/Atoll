using System.Text.Json.Serialization;
using Atoll.Api;
using Atoll.Api.Services.Metrics;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Runtime;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Search.Indexing;
using Atoll.Api.Services.Search.Refresh;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AtollOptions>()
    .Bind(builder.Configuration.GetSection("Atoll"))
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
builder.Services.AddSingleton<PackageIndexUpdater>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton(new ApplicationRuntimeInfo(DateTimeOffset.UtcNow));

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
    return new MongoClient(options.Mongo.ConnectionString);
});

builder.Services.AddSingleton<IPackageRepository, MongoPackageRepository>();
builder.Services.AddSingleton<IPackageService, MongoPackageService>();
builder.Services.AddSingleton<IGitTransferService, GitTransferService>();

builder.Services.AddHostedService<PackageIndexWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseExceptionHandler();
app.MapEndpoints();

await app.RunAsync();