using System.Text.Json.Serialization;
using Atoll.Api;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AtollOptions>(builder.Configuration);
builder.Services.Configure<JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<PackageIndexStore>();
builder.Services.AddSingleton<PackageQueryService>();
builder.Services.AddSingleton<PackageRefreshCoordinator>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton(new ApplicationRuntimeInfo(DateTimeOffset.UtcNow));

builder.Services.AddHostedService<PackageRefreshWorker>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapEndpoints();

await app.RunAsync();