using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Tests.Testing;

/// <summary>
/// Endpoints that exist only in tests, to exercise rules the real API does not use yet: every unsafe
/// HTTP method, a policy that always refuses (a 403), and the client address as the app sees it.
/// </summary>
public sealed record ProbeBody(string Name, int Count);

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

    // --- Hardening: bodies, limits, failures --------------------------------------------------

    /// <summary>A strict body: both fields, nothing else.</summary>
    [HttpPost("json")]
    [AllowAnonymous]
    public IActionResult Json([FromBody] ProbeBody body) => Ok(new { body.Name, body.Count });

    /// <summary>Any JSON at all, to try depth.</summary>
    [HttpPost("anything")]
    [AllowAnonymous]
    public IActionResult Anything([FromBody] System.Text.Json.JsonElement body) => Ok();

    /// <summary>An endpoint with a smaller body limit than the default.</summary>
    [HttpPost("limited")]
    [AllowAnonymous]
    [CaseRegister.Api.Hardening.RequestBodyLimit(32)]
    public IActionResult Limited([FromBody] System.Text.Json.JsonElement body) => Ok();

    /// <summary>An endpoint that has raised its own limit, as an evidence upload will.</summary>
    [HttpPost("generous")]
    [AllowAnonymous]
    [CaseRegister.Api.Hardening.RequestBodyLimit(1_000_000)]
    public IActionResult Generous([FromBody] System.Text.Json.JsonElement body) => Ok();

    /// <summary>Fails with a message full of things that must never reach a client.</summary>
    [HttpGet("throw")]
    [AllowAnonymous]
    public IActionResult Throw() => throw new InvalidOperationException($"SELECT secret FROM users WHERE nrc = '123456/78/9' failed on {HttpContext.Request.Path}");

    /// <summary>Never finishes on its own: only the request timeout ends it.</summary>
    [HttpGet("slow")]
    [AllowAnonymous]
    public async Task<IActionResult> Slow(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);

        return Ok();
    }

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
