using Atoll.Api.Extensions;
using Atoll.Api.Services.Catalog;
using Atoll.Api.Services.Catalog.Rpc;
using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Security;
using Atoll.Api.Services.Sync.Direct;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Atoll.Api;

public static class Endpoints
{
    private const string GetPackageEndpoint = "GetPackage";
    private const string GetPackageSecurityEndpoint = "GetPackageSecurity";

    public static void MapEndpoints(this WebApplication app)
    {
        app.MapMethods("/health", ["GET", "HEAD"], TypedResults.Ok);
        app.MapAurRpcEndpoints();

        MapGitProtocolRoutes(app.MapGroup("/packages"));
        MapGitProtocolRoutes(app.MapGroup(""));

        var api = app.NewVersionedApi().MapGroup("v{version:apiVersion}");
        var v1 = api.MapGroup("").HasApiVersion(1.0);

        v1.MapGet("/search", Search);
        MapPackageRoutes(v1.MapGroup("/packages"));
    }

    private static Ok<AurPackageMetadata[]> Search(
        [FromServices] PackageSearchService searchService,
        [FromQuery(Name = "query")] SearchQuery? query,
        [FromQuery(Name = "by")] ByQuery? by)
    {
        var queryValues = query?.Query.ToHashSet() ?? [];
        var byValue = by?.By ?? By.Name;

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
                async Task<Results<Created, ProblemHttpResult>> (
                    [FromRoute] string name,
                    [FromServices] DirectPackageSeeder seeder,
                    [FromServices] LinkGenerator links,
                    [FromServices] IOptions<AtollOptions> options,
                    HttpContext context) =>
                {
                    if (!options.Value.Mutations.Enabled)
                        return MutationsDisabled();

                    await seeder.SeedAsync(name);
                    return TypedResults.Created(GetRequiredPath(links, context, GetPackageEndpoint, new { name }));
                })
            .ProducesProblem(StatusCodes.Status403Forbidden);

        packages.MapGet("/{name}/versions",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetHistoryAsync(name)));

        packages.MapDelete("/{name}",
                async Task<Results<NoContent, ProblemHttpResult>> (
                    [FromRoute] string name,
                    [FromServices] IPackageService repo,
                    [FromServices] IOptions<AtollOptions> options) =>
                {
                    if (!options.Value.Mutations.Enabled) return MutationsDisabled();
                    await repo.DeleteAsync(name);
                    return TypedResults.NoContent();
                })
            .ProducesProblem(StatusCodes.Status403Forbidden);

        packages.MapGet("/{name}/security", SecurityStatus)
            .WithName(GetPackageSecurityEndpoint)
            .ProducesJsonOneOf<PackageSecurityHistoryResponse, PackageSecurityRevisionResponse>();
        packages.MapPost("/{name}/security/rescan", SecurityRescan)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var secured = packages
            .MapGroup("")
            .AddEndpointFilter<PackageSecurityFilter>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        secured.MapGet("/{name}",
                async ([FromRoute] string name, [FromServices] IPackageService repo) =>
                TypedResults.Ok(await repo.GetAsync(name)))
            .WithName(GetPackageEndpoint);

        secured.MapGet("/{name}/versions/{sha}",
            async ([FromRoute] string name, [FromRoute] string sha, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetAsync(name, sha)));
    }

    private static async
        Task<Results<Ok<PackageSecurityHistoryResponse>, Ok<PackageSecurityRevisionResponse>, NotFound>> SecurityStatus(
            [FromRoute] string name,
            [FromQuery(Name = "revision")] string? revision,
            [FromServices] PackageSecurityStatusService security,
            CancellationToken ct)
    {
        if (string.IsNullOrEmpty(revision))
        {
            var history = await security.GetHistoryAsync(name, ct);
            return history is null ? TypedResults.NotFound() : TypedResults.Ok(history);
        }

        var revisionStatus = await security.GetRevisionAsync(name, revision, ct);
        return revisionStatus is null ? TypedResults.NotFound() : TypedResults.Ok(revisionStatus);
    }

    private static async Task<Results<Accepted, NotFound, ProblemHttpResult>> SecurityRescan(
        [FromRoute] string name,
        [FromQuery(Name = "revision")] string? revision,
        [FromServices] PackageSecurityStatusService security,
        [FromServices] LinkGenerator links,
        [FromServices] IOptions<AtollOptions> options,
        HttpContext context)
    {
        if (!options.Value.Mutations.Enabled)
            return MutationsDisabled();

        var revisionId = await security.QueueRescanAsync(name, revision, context.RequestAborted);
        if (revisionId is null)
            return TypedResults.NotFound();

        return TypedResults.Accepted(GetRequiredPath(
            links,
            context,
            GetPackageSecurityEndpoint,
            new { name, revision = revisionId }));
    }

    private static string GetRequiredPath(
        LinkGenerator links,
        HttpContext context,
        string endpointName,
        object values) =>
        links.GetPathByName(context, endpointName, values) ??
        throw new InvalidOperationException($"Could not generate a path for endpoint '{endpointName}'.");

    private static ProblemHttpResult MutationsDisabled()
    {
        return TypedResults.Problem(
            "Mutating actions are disabled on this instance.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static void MapGitProtocolRoutes(RouteGroupBuilder packages)
    {
        var secured = packages
            .MapGroup("")
            .AddEndpointFilter<PackageSecurityFilter>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        secured.MapGet("/{name}.git/info/refs", GitInfoRefs);
        secured.MapPost("/{name}.git/git-upload-pack", GitUploadPack);
    }

    private static async Task<Results<EmptyHttpResult, NotFound, ProblemHttpResult>> GitInfoRefs(
        [FromRoute] string name,
        [FromQuery(Name = "service")] string? service,
        [FromServices] IGitTransferService git,
        HttpResponse response,
        CancellationToken ct)
    {
        response.Headers.CacheControl = GitSmartHttp.NoCacheControl;

        if (!GitSmartHttp.IsSupportedService(service))
            return TypedResults.Problem($"Only {GitSmartHttp.UploadPackService} is supported.",
                statusCode: StatusCodes.Status403Forbidden);

        response.ContentType = GitSmartHttp.AdvertisementMediaType;

        var result = await git.AdvertiseRefsAsync(name, response.Body, ct);
        if (result is GitTransferResult.Ok)
            return TypedResults.Empty;

        response.ContentType = null;
        return TypedResults.NotFound();
    }

    private static async Task<Results<EmptyHttpResult, NotFound>> GitUploadPack(
        [FromRoute] string name,
        [FromServices] IGitTransferService git,
        HttpRequest request,
        HttpResponse response,
        CancellationToken ct)
    {
        response.Headers.CacheControl = GitSmartHttp.NoCacheControl;

        response.ContentType = GitSmartHttp.UploadPackResultMediaType;

        var result = await git.UploadPackAsync(name, request.Body, response.Body, ct);
        if (result is GitTransferResult.Ok)
            return TypedResults.Empty;

        response.ContentType = null;
        return TypedResults.NotFound();
    }
}