using System.Text.Json.Serialization;
using Amazon.Runtime;
using Amazon.S3;
using Atoll.Api;
using Atoll.Api.Services.Aur;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AtollOptions>()
    .Bind(builder.Configuration.GetSection("Atoll"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

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
    var options = sp.GetRequiredService<IOptions<AtollOptions>>().Value;
    var s3 = options.Storage.S3;
    var credentials = new BasicAWSCredentials(s3.AccessKey, s3.SecretKey);
    var config = new AmazonS3Config
    {
        ForcePathStyle = s3.ForcePathStyle,
        UseHttp = s3.UseHttp,
        AuthenticationRegion = s3.Region
    };

    if (!string.IsNullOrEmpty(s3.Endpoint))
        config.ServiceURL = $"{(s3.UseHttp ? "http" : "https")}://{s3.Endpoint}";

    return new AmazonS3Client(credentials, config);
});

builder.Services.AddSingleton<IBundleStorage>(sp =>
{
    var options = sp.GetRequiredService<IOptions<AtollOptions>>();
    return options.Value.Storage.Type switch
    {
        StorageType.S3 => new S3BundleStorage(
            sp.GetRequiredService<IAmazonS3>(), options),
        StorageType.Local => new LocalBundleStorage(options),
        _ => throw new NotSupportedException(
            $"Storage type {options.Value.Storage.Type} is not supported")
    };
});

builder.Services.AddHostedService<PackageRefreshWorker>();
builder.Services.AddHostedService<PackageSyncS3Worker>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapEndpoints();

await app.RunAsync();