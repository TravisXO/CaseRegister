using System.Security.Claims;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CaseRegister.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    ISignInService signInService,
    IAccountPasswordService passwordService,
    IAntiforgery antiforgery) : ControllerBase
{
    private const string SignInFailedDetail = "Sign-in failed. Check your details and try again.";

    /// <summary>Issues an anti-forgery token. Send it back in the X-CSRF-TOKEN header on every POST, PUT, PATCH and DELETE.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.CacheControl = "no-store";

        return Ok(new { token = tokens.RequestToken });
    }

    [HttpPost("sign-in")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.SignInPerIp)]
    [ServiceFilter<SignInAccountRateLimitFilter>]
    public async Task<IActionResult> SignIn(SignInCommand command, CancellationToken cancellationToken)
    {
        var status = await signInService.SignInAsync(command, RemoteIp(), cancellationToken);

        // One failure response whatever the reason, so sign-in reveals nothing about any account.
        return status == SignInStatus.Succeeded
            ? Ok(new { status = "signed-in" })
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: SignInFailedDetail);
    }

    [HttpPost("sign-out")]
    [Authorize]
    public async Task<IActionResult> EndSession()
    {
        await signInService.SignOutAsync();

        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var result = await passwordService.ChangePasswordAsync(userId, command, RemoteIp(), cancellationToken);

        if (result.Succeeded)
        {
            return NoContent();
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "The password was not changed",
            Detail = string.Join(' ', result.Errors),
        };
        problem.Extensions["errors"] = result.Errors;

        return BadRequest(problem);
    }

    /// <summary>Tells the front end whether a session is open, and whose it is.</summary>
    [HttpGet("session")]
    [Authorize]
    public IActionResult Session() => Ok(new { userId = User.FindFirstValue(ClaimTypes.NameIdentifier) });

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
