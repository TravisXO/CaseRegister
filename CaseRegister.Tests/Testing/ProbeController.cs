using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    /// <summary>Shows a "sensitive record": every successful view is written to the audit log.</summary>
    [HttpGet("audited/{id}")]
    [CaseRegister.Api.Security.AuditView("Probe")]
    public IActionResult Audited(string id) => Ok(new { id });

    /// <summary>The same, but the record is not found: nothing was shown, so nothing is recorded.</summary>
    [HttpGet("audited-missing/{id}")]
    [CaseRegister.Api.Security.AuditView("Probe")]
    public IActionResult AuditedMissing(string id) => NotFound();

    [HttpGet("unannotated")]
    public IActionResult Unannotated() => Ok();

    [HttpGet("forbidden")]
    [Authorize(Policy = NeverPolicy)]
    public IActionResult Forbidden() => Ok();

    /// <summary>Evaluates a named policy against the signed-in user's session: 200 if it passes, 403 if not.</summary>
    [HttpGet("can/{policy}")]
    [Authorize(Policy = "SignedIn")]
    public async Task<IActionResult> Can(string policy, [FromServices] IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(User, policy)).Succeeded ? Ok() : StatusCode(StatusCodes.Status403Forbidden);
}
