using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AtollOptions>(builder.Configuration);
#pragma warning disable IL2026 // Controllers are intentional for this app; MVC isn't trim-safe.
builder.Services.AddControllers();
#pragma warning restore IL2026
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
app.MapControllers();

await app.RunAsync();

public partial class Program;