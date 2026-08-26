using Atoll.Api.Services.Catalog.Rpc;
using Atoll.Api.Services.Packages.Persistence;

namespace Atoll.Api.Services.Security;

public sealed class PackageSecurityFilter(
    IPackageSecurityAccess security,
    IPackageRepository packages,
    AurRpcService rpc) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var name = context.HttpContext.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
            return await next(context);

        var sha = context.HttpContext.GetRouteValue("sha")?.ToString();
        var packageName = name;

        if (context.HttpContext.Request.Path.Value?.Contains(".git/", StringComparison.Ordinal) == true)
        {
            foreach (var candidate in rpc.ResolvePackageNames(name))
            {
                if (!await packages.ExistsAsync(candidate, context.HttpContext.RequestAborted))
                    continue;

                packageName = candidate;
                break;
            }
        }

        var access = await security.CheckAsync(
            packageName,
            string.IsNullOrEmpty(sha) ? null : sha,
            context.HttpContext.RequestAborted);
        if (!access.Allowed)
            return TypedResults.Problem(
                "This package is unavailable until it passes security checks.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["reason"] = access.ReasonCode });

        return await next(context);
    }
}