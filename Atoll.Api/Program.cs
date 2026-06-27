using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Atoll.Api;
using Atoll.Api.Services.Aur;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

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
builder.Services.AddSingleton<IPackageRepository, GitPackageRepository>();
builder.Services.AddSingleton<IAmazonS3, AmazonS3Client>(sp =>
{
    var atollOptions = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
    var awsCredentials = new BasicAWSCredentials(atollOptions.S3AccessKey, atollOptions.S3SecretKey);
    var s3Config = new AmazonS3Config
    {
        ForcePathStyle = atollOptions.S3ForcePathStyle,
        UseHttp = atollOptions.S3UseHttp,
        AuthenticationRegion = atollOptions.S3Region
    };

    if (atollOptions.S3Endpoint == string.Empty) return new AmazonS3Client(awsCredentials, s3Config);
    var protocol = atollOptions.S3UseHttp ? "http" : "https";
    s3Config.ServiceURL = $"{protocol}://{atollOptions.S3Endpoint}";
    return new AmazonS3Client(awsCredentials, s3Config);
});

builder.Services.AddSingleton<IBundleStorage>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AtollOptions>>();
    return options.Value.StorageType switch
    {
        StorageType.S3 => new S3BundleStorage(sp.GetRequiredService<IAmazonS3>(), options),
        StorageType.Local => new LocalBundleStorage(options),
        _ => throw new NotSupportedException($"Storage type {options.Value.StorageType} is not supported")
    };
});

builder.Services.AddHostedService<PackageRefreshWorker>();
builder.Services.AddHostedService<PackageSyncS3Worker>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapEndpoints();

await app.RunAsync();