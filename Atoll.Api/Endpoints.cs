using Atoll.Api.Services.Metrics;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Git;
using Atoll.Api.Services.Search;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapMethods("/health", ["GET", "HEAD"], TypedResults.Ok);
        app.MapGet("/metrics", Metrics);
        app.MapGet("/search", Search);

        var packages = app.MapGroup("/packages");
        MapPackageRoutes(packages);
        MapGitProtocolRoutes(packages);

        app.MapFallback("/{**path}", ([FromRoute] string? path) => TypedResults.NotFound());
    }

    private static Ok<Metrics> Metrics([FromServices] MetricsService metricsService)
    {
        return TypedResults.Ok(metricsService.GetMetrics());
    }

    private static Ok<AurPackageMetadata[]> Search(
        [FromServices] PackageSearchService searchService,
        [FromQuery(Name = "query")] ValuesQuery? query,
        [FromQuery(Name = "by")] ByQuery? by)
    {
        var queryValues = query?.Values.ToHashSet() ?? [];
        var byValue = by?.Value ?? By.Name;

        return byValue switch
        {
            By.Name => TypedResults.Ok(searchService.FindByNames(queryValues)),
            By.Words => TypedResults.Ok(searchService.FindByWords(queryValues)),
            By.Provides => TypedResults.Ok(searchService.FindByProvides(queryValues)),
            _ => throw new ArgumentOutOfRangeException(nameof(by), by, null)
        };
    }

    private static void MapPackageRoutes(RouteGroupBuilder packages)
    {
        packages.MapGet("",
            async ([FromServices] IPackageService repo) => TypedResults.Ok(await repo.ListAsync()));

        packages.MapPost("/{name}/seed",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            {
                await repo.SeedFromAurAsync(name);
                return TypedResults.Created($"/packages/{name}");
            });

        packages.MapGet("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetAsync(name)));

        packages.MapGet("/{name}/versions",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetHistoryAsync(name)));

        packages.MapGet("/{name}/versions/{sha}",
            async ([FromRoute] string name, [FromRoute] string sha, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetAsync(name, sha)));

        packages.MapDelete("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            {
                await repo.DeleteAsync(name);
                return TypedResults.NoContent();
            });
    }

    private static void MapGitProtocolRoutes(RouteGroupBuilder packages)
    {
        packages.MapGet("/{name}.git/info/refs", GitInfoRefs);
        packages.MapPost("/{name}.git/git-upload-pack", GitUploadPack);
    }

    private static async Task<IResult> GitInfoRefs(
        [FromRoute] string name,
        [FromQuery(Name = "service")] string? service,
        [FromServices] IGitTransferService git,
        HttpResponse response,
        CancellationToken ct)
    {
        response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";

        if (!string.Equals(service, "git-upload-pack", StringComparison.Ordinal))
            return TypedResults.Problem("Only git-upload-pack is supported.", statusCode: StatusCodes.Status403Forbidden);

        response.ContentType = "application/x-git-upload-pack-advertisement";

        var result = await git.AdvertiseRefsAsync(name, response.Body, ct);
        if (result is GitTransferResult.Ok)
            return TypedResults.Empty;

        response.ContentType = null;
        return TypedResults.NotFound();
    }

    private static async Task<IResult> GitUploadPack(
        [FromRoute] string name,
        [FromServices] IGitTransferService git,
        HttpRequest request,
        HttpResponse response,
        CancellationToken ct)
    {
        response.Headers.CacheControl = "no-cache, max-age=0, must-revalidate";
        response.ContentType = "application/x-git-upload-pack-result";

        var result = await git.UploadPackAsync(name, request.Body, response.Body, ct);
        if (result is GitTransferResult.Ok)
            return TypedResults.Empty;

        response.ContentType = null;
        return TypedResults.NotFound();
    }
}