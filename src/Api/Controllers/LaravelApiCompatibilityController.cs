using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api")]
public sealed class LaravelApiCompatibilityController : ControllerBase
{
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE")]
    [Route("{**path}", Order = 9999)]
    public IActionResult HandleLaravelApiRoute(string? path)
    {
        var normalizedPath = (path ?? string.Empty).Trim('/');
        return NotFound(new
        {
            status = "error",
            message = "API endpoint not found. Check the URL and HTTP method.",
            endpoint = $"/api/{normalizedPath}",
            method = Request.Method,
            errors = new { route = new[] { "No matching API route is registered." } }
        });
    }
}
