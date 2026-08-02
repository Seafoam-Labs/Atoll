namespace Atoll.Api.Services.Security;

public sealed class PackageSecurityFilter(IPackageSecurityAccess security) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var name = context.HttpContext.GetRouteValue("name")?.ToString();
        if (string.IsNullOrEmpty(name))
            return await next(context);

        var access = await security.CheckAsync(name, context.HttpContext.RequestAborted);
        if (!access.Allowed)
            return TypedResults.Problem(
                "This package is unavailable until it passes security checks.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["reason"] = access.ReasonCode });

        return await next(context);
    }
}