using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api.Controllers;

[ApiController]
public sealed class FallbackController : ControllerBase
{
    [Route("{*path}", Order = int.MaxValue)]
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult CatchAll()
    {
        return new ContentResult
        {
            Content = "That route does not exist.",
            ContentType = "text/html",
            StatusCode = StatusCodes.Status404NotFound
        };
    }
}
