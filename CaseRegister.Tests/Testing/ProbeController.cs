using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Tests.Testing;

/// <summary>
/// Endpoints that exist only in tests, to exercise rules the real API does not use yet: every unsafe
/// HTTP method, a policy that always refuses (a 403), and the client address as the app sees it.
/// </summary>
[ApiController]
[Route("probe")]
public sealed class ProbeController : ControllerBase
{
    public const string NeverPolicy = "Never";

    [HttpPost]
    [AllowAnonymous]
    public IActionResult Post() => NoContent();

    [HttpPut]
    [AllowAnonymous]
    public IActionResult Put() => NoContent();

    [HttpPatch]
    [AllowAnonymous]
    public IActionResult Patch() => NoContent();

    [HttpDelete]
    [AllowAnonymous]
    public IActionResult Delete() => NoContent();

    [HttpGet("ip")]
    [AllowAnonymous]
    public IActionResult Ip() => Ok(new { ip = HttpContext.Connection.RemoteIpAddress?.ToString() });

    [HttpGet("members-only")]
    [Authorize]
    public IActionResult MembersOnly() => Ok();

    [HttpGet("forbidden")]
    [Authorize(Policy = NeverPolicy)]
    public IActionResult Forbidden() => Ok();
}
