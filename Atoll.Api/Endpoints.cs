using Atoll.Api.Services.Aur;
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
        DefinePackageRoutes(packages);

        app.MapFallback("/{**path}", ([FromRoute] string? path) => TypedResults.NotFound());
    }

    private static Ok<Metrics> Metrics([FromServices] MetricsService metricsService)
    {
        return TypedResults.Ok(metricsService.GetMetrics());
    }

    private static Ok<AurPackage[]> Search(
        [FromServices] PackageQueryService queryService,
        [FromQuery(Name = "query")] ValuesQuery? query,
        [FromQuery(Name = "by")] ByQuery? by)
    {
        var queryValues = query?.Values.ToHashSet() ?? [];
        var byValue = by?.Value ?? By.Name;

        return byValue switch
        {
            By.Name => TypedResults.Ok(queryService.FindByNames(queryValues)),
            By.Words => TypedResults.Ok(queryService.FindByWords(queryValues)),
            By.Provides => TypedResults.Ok(queryService.FindByProvides(queryValues)),
            _ => throw new ArgumentOutOfRangeException(nameof(by), by, null)
        };
    }

    private static void DefinePackageRoutes(RouteGroupBuilder packages)
    {
        packages.MapGet("",
            async ([FromServices] IPackageRepository repo) => TypedResults.Ok(await repo.ListAsync()));

        packages.MapPost("/{name}/seed",
            async ([FromRoute] string name, [FromServices] IPackageRepository repo) =>
            {
                await repo.SeedFromAurAsync(name);
                return TypedResults.Created($"/packages/{name}");
            });

        packages.MapGet("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageRepository repo) =>
            TypedResults.Ok(await repo.GetAsync(name)));

        packages.MapGet("/{name}/versions",
            async ([FromRoute] string name, [FromServices] IPackageRepository repo) =>
            TypedResults.Ok(await repo.GetHistoryAsync(name)));

        packages.MapGet("/{name}/versions/{sha}",
            async (
                [FromRoute] string name,
                [FromRoute] string sha,
                [FromServices] IPackageRepository repo) => TypedResults.Ok(await repo.GetAsync(name, sha)));

        packages.MapDelete("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageRepository repo) =>
            {
                await repo.DeleteAsync(name);
                return TypedResults.NoContent();
            });
    }
}