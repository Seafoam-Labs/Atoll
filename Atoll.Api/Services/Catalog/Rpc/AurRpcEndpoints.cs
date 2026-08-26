using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Primitives;

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
        app.MapMethods("/rpc", ["GET", "POST"], LegacyRpc)
            .DisableAntiforgery();
        app.MapGet("/rpc/v5/info", PathInfo);
        app.MapGet("/rpc/v5/info/{arg}", PathInfoWithArg);
        app.MapGet("/rpc/v5/search/{arg}", PathSearch);
        app.MapGet("/rpc/v5/search", PathSearchWithoutArg);
        app.MapGet("/rpc/v5/suggest/{arg}", PathSuggest);
        app.MapGet("/rpc/v5/suggest-pkgbase/{arg}", PathSuggestPackageBase);
    }

    private static async Task<IResult> LegacyRpc(HttpRequest request, AurRpcService rpc)
    {
        var form = request.HasFormContentType
            ? await request.ReadFormAsync(request.HttpContext.RequestAborted)
            : null;

        var version = FirstValue(request, form, "v");
        if (version is null)
            return JsonError("Please specify an API version.");
        if (!string.Equals(version, "5", StringComparison.Ordinal))
            return JsonError("Invalid version specified.");

        var type = FirstValue(request, form, "type");
        if (string.IsNullOrEmpty(type))
            return JsonError("No request type/data specified.");

        var arguments = Arguments(request, form);
        return type switch
        {
            "info" or "multiinfo" => Info(rpc, arguments),
            "search" => Search(rpc, arguments.FirstOrDefault(),
                FirstValue(request, form, "by") ?? "name-desc"),
            "msearch" => Search(rpc, arguments.FirstOrDefault() ?? string.Empty, "maintainer"),
            "suggest" => Suggest(rpc, arguments.FirstOrDefault(), false),
            "suggest-pkgbase" => Suggest(rpc, arguments.FirstOrDefault(), true),
            _ => JsonError("Incorrect request type specified.")
        };
    }

    private static IResult PathInfo(HttpRequest request, AurRpcService rpc)
    {
        return Info(rpc, Arguments(request));
    }

    private static IResult PathInfoWithArg(string arg, HttpRequest request, AurRpcService rpc)
    {
        return Info(rpc, [arg, .. Arguments(request)]);
    }

    private static IResult PathSearch(string arg, HttpRequest request, AurRpcService rpc)
    {
        return Search(rpc, arg, request.Query["by"].FirstOrDefault() ?? "name-desc");
    }

    private static IResult PathSearchWithoutArg(HttpRequest request, AurRpcService rpc)
    {
        return Search(rpc, string.Empty, request.Query["by"].FirstOrDefault() ?? "name-desc");
    }

    private static IResult PathSuggest(string arg, AurRpcService rpc)
    {
        return Suggest(rpc, arg, false);
    }

    private static IResult PathSuggestPackageBase(string arg, AurRpcService rpc)
    {
        return Suggest(rpc, arg, true);
    }

    private static IResult Info(AurRpcService rpc, IReadOnlyList<string> arguments)
    {
        return arguments.Count == 0
            ? JsonError("No request type/data specified.")
            : TypedResults.Json(AurRpcResponse.Success("multiinfo", rpc.Info(arguments)));
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
        return results.Count > AurRpcService.MaxResults
            ? JsonError("Too many package results.")
            : TypedResults.Json(AurRpcResponse.Success("search", results));
    }

    private static IResult Suggest(AurRpcService rpc, string? argument, bool packageBases)
    {
        if (string.IsNullOrEmpty(argument))
            return JsonError("No request type/data specified.");

        return TypedResults.Json(rpc.Suggest(argument, packageBases));
    }

    private static string[] Arguments(HttpRequest request, IFormCollection? form = null)
    {
        return
        [
            .. Values(request, form, "arg").Where(value => !string.IsNullOrEmpty(value)).Select(value => value!),
            .. Values(request, form, "arg[]").Where(value => !string.IsNullOrEmpty(value)).Select(value => value!)
        ];
    }

    private static string? FirstValue(HttpRequest request, IFormCollection? form, string key)
    {
        return Values(request, form, key).FirstOrDefault();
    }

    private static IEnumerable<string?> Values(HttpRequest request, IFormCollection? form, string key)
    {
        return request.Query[key].Concat(form?[key] ?? StringValues.Empty);
    }

    private static JsonHttpResult<AurRpcResponse> JsonError(string error)
    {
        return TypedResults.Json(AurRpcResponse.Failure(error));
    }
}