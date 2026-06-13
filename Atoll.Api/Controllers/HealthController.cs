using Microsoft.AspNetCore.Mvc;

namespace Atoll.Api.Controllers;

[ApiController]
public sealed class HealthController : ControllerBase
{
    [HttpGet("/health")]
    [HttpHead("/health")]
    public IActionResult Health()
    {
        return Ok();
    }
}
