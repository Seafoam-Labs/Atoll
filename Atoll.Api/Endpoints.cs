using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapMethods("/health", ["GET", "HEAD"], TypedResults.Ok);
        app.MapGet("/packages", Packages);
        app.MapGet("/metrics", Metrics);

        app.MapFallback("/{**path}", ([FromRoute] string? path) => TypedResults.NotFound());
    }

    private static Ok<AurPackage[]> Packages(
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

    private static Ok<Metrics> Metrics([FromServices] MetricsService metricsService)
    {
        return TypedResults.Ok(metricsService.GetMetrics());
    }
}