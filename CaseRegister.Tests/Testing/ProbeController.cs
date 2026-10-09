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

    /// <summary>Reachable by anyone signed in, even before two-factor: the named, weaker policy.</summary>
    [HttpGet("signed-in-only")]
    [Authorize(Policy = "SignedIn")]
    public IActionResult SignedInOnly() => Ok();

    /// <summary>No attribute at all: it must still be protected, by the fallback policy.</summary>
    [HttpGet("unannotated")]
    public IActionResult Unannotated() => Ok();

    [HttpGet("forbidden")]
    [Authorize(Policy = NeverPolicy)]
    public IActionResult Forbidden() => Ok();
}
