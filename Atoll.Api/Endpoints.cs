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

    private static IResult Packages(
        [FromServices] PackageQueryService queryService,
        [FromQuery(Name = "query")] QueryValues? query,
        [FromQuery(Name = "by")] QueryType? by)
    {
        var queryValues = query?.Values.ToHashSet() ?? [];
        by ??= QueryType.Name;

        return by switch
        {
            QueryType.Name => TypedResults.Ok(queryService.FindByNames(queryValues)),
            QueryType.Desc => TypedResults.Ok(queryService.FindByWords(queryValues)),
            QueryType.Prov => TypedResults.Ok(queryService.FindByProvides(queryValues)),
            _ => TypedResults.BadRequest()
        };
    }

    private static Ok<MetricsResponse> Metrics([FromServices] MetricsService metricsService)
    {
        return TypedResults.Ok(metricsService.GetMetrics());
    }
}