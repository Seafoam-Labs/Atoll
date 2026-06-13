using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api.Controllers;

[ApiController]
public sealed class PackagesController(PackageQueryService queryService) : ControllerBase
{
    [HttpGet("/packages")]
    public IActionResult Packages([FromQuery] string? names, [FromQuery] string? by)
    {
        var parsedNames = QueryParsing.ParseNames(names);

        return by switch
        {
            "prov" => Ok(queryService.FindByProvides(parsedNames)),
            "desc" => Ok(queryService.FindByWords(parsedNames)),
            null or "" => Ok(queryService.FindByNames(parsedNames)),
            _ => new ContentResult
            {
                Content = "That route does not exist.",
                ContentType = "text/html",
                StatusCode = StatusCodes.Status404NotFound
            }
        };
    }
}
