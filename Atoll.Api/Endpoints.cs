using Atoll.Api.Services.Git;
using Atoll.Api.Services.Packages;
using Atoll.Api.Services.Packages.Seed;
using Atoll.Api.Services.Search;
using Atoll.Api.Services.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Atoll.Api;

public static class Endpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.MapMethods("/health", ["GET", "HEAD"], TypedResults.Ok);
        app.MapGet("/search", Search);

        var packages = app.MapGroup("/packages");
        MapPackageRoutes(packages);
        MapGitProtocolRoutes(packages);
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
            async ([FromRoute] string name, [FromServices] DirectPackageSeeder seeder,
                [FromServices] IOptions<AtollOptions> options) =>
            {
                if (!options.Value.Mutations.Enabled)
                    return MutationsDisabled();

                await seeder.SeedAsync(name);
                return TypedResults.Created($"/packages/{name}");
            });

        packages.MapGet("/{name}/versions",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetHistoryAsync(name)));

        packages.MapDelete("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageService repo,
                [FromServices] IOptions<AtollOptions> options) =>
            {
                if (!options.Value.Mutations.Enabled) return MutationsDisabled();
                await repo.DeleteAsync(name);
                return TypedResults.NoContent();
            });

        packages.MapGet("/{name}/security", SecurityStatus);
        packages.MapPost("/{name}/security/rescan", SecurityRescan);

        var secured = packages
            .MapGroup("")
            .AddEndpointFilter<PackageSecurityFilter>();

        secured.MapGet("/{name}",
            async ([FromRoute] string name, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetAsync(name)));

        secured.MapGet("/{name}/versions/{sha}",
            async ([FromRoute] string name, [FromRoute] string sha, [FromServices] IPackageService repo) =>
            TypedResults.Ok(await repo.GetAsync(name, sha)));
    }

    private static async Task<IResult> SecurityStatus(
        [FromRoute] string name,
        [FromQuery(Name = "revision")] string? revision,
        [FromServices] IPackageRepository packages,
        [FromServices] IPackageSecurityRepository security)
    {
        var package = await packages.GetHeadAsync(name);
        if (package is null)
            return TypedResults.NotFound();

        if (string.IsNullOrEmpty(revision))
        {
            var scans = await security.ListForPackageAsync(name);
            return TypedResults.Ok(new
            {
                packageName = name,
                headRevisionId = package.HeadRevisionId,
                revisions = scans
                    .OrderByDescending(s => s.IsHead)
                    .ThenByDescending(s => s.ScannedAt)
                    .Select(s => new
                    {
                        revisionId = s.RevisionId,
                        status = s.Status.ToString(),
                        isHead = s.IsHead,
                        scannedAt = s.ScannedAt,
                        findingCount = s.Findings.Count
                    })
            });
        }

        var scan = await security.GetAsync(name, revision);
        return TypedResults.Ok(new
        {
            packageName = name,
            revisionId = revision,
            status = (scan?.Status ?? Services.Security.SecurityStatus.Pending).ToString(),
            isHead = revision == package.HeadRevisionId,
            scannedAt = scan?.ScannedAt,
            findingCount = scan?.Findings.Count ?? 0
        });
    }

    private static async Task<IResult> SecurityRescan(
        [FromRoute] string name,
        [FromQuery(Name = "revision")] string? revision,
        [FromServices] IPackageRepository packages,
        [FromServices] IPackageSecurityRepository security,
        [FromServices] IOptions<AtollOptions> options)
    {
        if (!options.Value.Mutations.Enabled)
            return MutationsDisabled();

        var package = await packages.GetHeadAsync(name);
        if (package is null)
            return TypedResults.NotFound();

        var revisionId = string.IsNullOrEmpty(revision) ? package.HeadRevisionId : revision;
        if (package.Revisions.All(r => r.RevisionId != revisionId))
            return TypedResults.NotFound();

        await security.MarkPendingAsync(name, revisionId, revisionId == package.HeadRevisionId);
        return TypedResults.Accepted($"/packages/{name}/security?revision={revisionId}");
    }

    private static IResult MutationsDisabled()
    {
        return TypedResults.Problem(
            "Mutating actions are disabled on this instance.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    private static void MapGitProtocolRoutes(RouteGroupBuilder packages)
    {
        var secured = packages
            .MapGroup("")
            .AddEndpointFilter<PackageSecurityFilter>();

        secured.MapGet("/{name}.git/info/refs", GitInfoRefs);
        secured.MapPost("/{name}.git/git-upload-pack", GitUploadPack);
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