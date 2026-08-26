using Atoll.Api;
using Atoll.Api.Components;
using Atoll.Api.Extensions;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

builder.Services.AddAtollOptions(builder.Configuration);
builder.Services.AddAtollInfrastructure();
builder.Services.AddAtollObservability();
builder.Services.AddCatalogServices();
builder.Services.AddPackageServices();
builder.Services.AddGitServices();
builder.Services.AddSecurityServices(builder.Configuration);
builder.Services.AddSyncServices(builder.Configuration);
builder.Services.AddUiServices();

builder.Logging.AddOpenTelemetry();

var app = builder.Build();

app.UseExceptionHandler();
app.UseResponseCompression();

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
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseRouting();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapEndpoints();
app.MapPrometheusScrapingEndpoint();

app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();