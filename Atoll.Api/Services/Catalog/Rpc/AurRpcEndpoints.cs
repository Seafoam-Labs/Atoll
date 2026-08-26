using Microsoft.AspNetCore.Http.HttpResults;

namespace Atoll.Api.Services.Catalog.Rpc;

public static class AurRpcEndpoints
{
    private static readonly HashSet<string> SearchFields =
    [
        "name", "name-desc", "maintainer", "comaintainers", "depends", "makedepends",
        "optdepends", "checkdepends", "provides", "conflicts", "replaces", "groups", "submitter"
    ];

    public static void MapAurRpcEndpoints(this WebApplication app)
    {
        app.MapGet("/rpc", LegacyRpc);
        app.MapGet("/rpc/v5/info", PathInfo);
        app.MapGet("/rpc/v5/info/{arg}", PathInfoWithArg);
        app.MapGet("/rpc/v5/search/{arg}", PathSearch);
        app.MapGet("/rpc/v5/search", PathSearchWithoutArg);
        app.MapGet("/rpc/v5/suggest/{arg}", PathSuggest);
        app.MapGet("/rpc/v5/suggest-pkgbase/{arg}", PathSuggestPackageBase);
    }

    private static IResult LegacyRpc(HttpRequest request, AurRpcService rpc)
    {
        var version = request.Query["v"].FirstOrDefault();
        if (version is null)
            return JsonError("Please specify an API version.");
        if (!string.Equals(version, "5", StringComparison.Ordinal))
            return JsonError("Invalid version specified.");

        var type = request.Query["type"].FirstOrDefault();
        if (string.IsNullOrEmpty(type))
            return JsonError("No request type/data specified.");

        var arguments = Arguments(request);
        return type switch
        {
            "info" or "multiinfo" => Info(rpc, arguments),
            "search" => Search(rpc, arguments.FirstOrDefault(),
                request.Query["by"].FirstOrDefault() ?? "name-desc"),
            "msearch" => Search(rpc, arguments.FirstOrDefault() ?? string.Empty, "maintainer"),
            "suggest" => Suggest(rpc, arguments.FirstOrDefault(), false),
            "suggest-pkgbase" => Suggest(rpc, arguments.FirstOrDefault(), true),
            _ => JsonError("Incorrect request type specified.")
        };
    }

    private static IResult PathInfo(HttpRequest request, AurRpcService rpc) => Info(rpc, Arguments(request));

    private static IResult PathInfoWithArg(string arg, HttpRequest request, AurRpcService rpc) =>
        Info(rpc, [arg, .. Arguments(request)]);

    private static IResult PathSearch(string arg, HttpRequest request, AurRpcService rpc) =>
        Search(rpc, arg, request.Query["by"].FirstOrDefault() ?? "name-desc");

    private static IResult PathSearchWithoutArg(HttpRequest request, AurRpcService rpc) =>
        Search(rpc, string.Empty, request.Query["by"].FirstOrDefault() ?? "name-desc");

    private static IResult PathSuggest(string arg, AurRpcService rpc) => Suggest(rpc, arg, false);

    private static IResult PathSuggestPackageBase(string arg, AurRpcService rpc) => Suggest(rpc, arg, true);

    private static IResult Info(AurRpcService rpc, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return JsonError("No request type/data specified.");

        return TypedResults.Json(AurRpcResponse.Success("multiinfo", rpc.Info(arguments)));
    }

    private static IResult Search(AurRpcService rpc, string? argument, string by)
    {
        if (!SearchFields.Contains(by))
            return JsonError("Incorrect by field specified.");

        var allowsEmpty = string.Equals(by, "maintainer", StringComparison.Ordinal);
        if (argument is null || (!allowsEmpty && argument.Length == 0))
            return JsonError("No request type/data specified.");
        if (!allowsEmpty && argument.Length < 2)
            return JsonError("Query arg too small.");

        var results = rpc.Search(argument, by);
        if (results.Count > AurRpcService.MaxResults)
            return JsonError("Too many package results.");

        return TypedResults.Json(AurRpcResponse.Success("search", results));
    }

    private static IResult Suggest(AurRpcService rpc, string? argument, bool packageBases)
    {
        if (string.IsNullOrEmpty(argument))
            return JsonError("No request type/data specified.");

        return TypedResults.Json(rpc.Suggest(argument, packageBases));
    }

    private static string[] Arguments(HttpRequest request) =>
    [
        .. request.Query["arg"].Where(value => !string.IsNullOrEmpty(value)).Select(value => value!),
        .. request.Query["arg[]"].Where(value => !string.IsNullOrEmpty(value)).Select(value => value!)
    ];

    private static JsonHttpResult<AurRpcResponse> JsonError(string error) =>
        TypedResults.Json(AurRpcResponse.Failure(error));
}
