using CaseRegister.Api.Hardening;
using System.Security.Claims;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// Authorisation: anything with no attribute, and a bare [Authorize], needs a user who has completed
/// two-factor. The few endpoints a half-signed-in user may reach say so by name (SignedIn), and the
/// ones that must work before anyone is signed in are marked AllowAnonymous.
/// </remarks>
[ApiController]
[Route("api/auth")]
[RequestBodyLimit(RequestLimits.AuthBodyBytes)]
public sealed class AuthController(
    ISignInService signInService,
    IAccountPasswordService passwordService,
    ITwoFactorService twoFactor,
    ISessionService sessions,
    IAntiforgery antiforgery) : ControllerBase
{
    private const string SignInFailedDetail = "Sign-in failed. Check your details and try again.";
    private const string VerificationFailedDetail = "Verification failed. Check the code and try again.";

    /// <summary>Issues an anti-forgery token. Send it back in the X-CSRF-TOKEN header on every POST, PUT, PATCH and DELETE.</summary>
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Headers.CacheControl = "no-store";

        return Ok(new { token = tokens.RequestToken });
    }

    // --- Signing in --------------------------------------------------------------------------

    /// <summary>Step one: the password. A right password never opens a session by itself; it leads to a code, or to enrolment.</summary>
    [HttpPost("sign-in")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.SignInPerIp)]
    [ServiceFilter<SignInAccountRateLimitFilter>]
    public async Task<IActionResult> SignIn(SignInCommand command, CancellationToken cancellationToken)
    {
        var status = await signInService.SignInAsync(command, RemoteIp(), cancellationToken);

        // One failure response whatever the reason, so sign-in reveals nothing about any account.
        return status switch
        {
            SignInStatus.TwoFactorRequired => Ok(new { status = "two-factor-required" }),
            SignInStatus.TwoFactorEnrolmentRequired => Ok(new { status = "two-factor-enrolment-required" }),
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: SignInFailedDetail),
        };
    }

    /// <summary>Step two: the code from the authenticator app (or a recovery code). Only works right after step one.</summary>
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyTwoFactor(TwoFactorCodeCommand command, CancellationToken cancellationToken)
    {
        var result = await twoFactor.VerifySignInAsync(command.Code, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            TwoFactorOutcome.Succeeded => Ok(new { status = "signed-in" }),
            TwoFactorOutcome.RateLimited => TooManyRequests.Result(HttpContext, result.RetryAfter ?? TimeSpan.FromMinutes(1)),
            _ => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Verification failed", detail: VerificationFailedDetail),
        };
    }

    [HttpPost("sign-out")]
    [Authorize(Policy = AuthorizationPolicies.SignedIn)]
    public async Task<IActionResult> EndSession()
    {
        await signInService.SignOutAsync();

        return NoContent();
    }

    /// <summary>Tells the front end whether a session is open, whose it is, and whether it still has to enrol in or pass two-factor.</summary>
    [HttpGet("session")]
    [Authorize(Policy = AuthorizationPolicies.SignedIn)]
    public IActionResult Session() => Ok(new
    {
        userId = User.FindFirstValue(ClaimTypes.NameIdentifier),
        twoFactorComplete = User.HasClaim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor),
    });

    // --- Two-factor enrolment ----------------------------------------------------------------

    /// <summary>Makes a key and returns the otpauth address for the QR code. Nothing is switched on yet.</summary>
    [HttpPost("2fa/enrol")]
    [Authorize(Policy = AuthorizationPolicies.SignedIn)]
    public async Task<IActionResult> StartEnrolment(CancellationToken cancellationToken)
    {
        var start = await twoFactor.StartEnrolmentAsync(CurrentUserId(), cancellationToken);

        return start is null
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "Two-factor is already set up", detail: "Two-factor authentication is already switched on for this account.")
            : Ok(new { otpAuthUri = start.OtpAuthUri, sharedKey = start.SharedKey });
    }

    /// <summary>Switches two-factor on, after a valid code. The recovery codes in the answer are shown once and never again.</summary>
    [HttpPost("2fa/enrol/confirm")]
    [Authorize(Policy = AuthorizationPolicies.SignedIn)]
    public async Task<IActionResult> ConfirmEnrolment(TwoFactorCodeCommand command, CancellationToken cancellationToken)
    {
        var result = await twoFactor.ConfirmEnrolmentAsync(CurrentUserId(), command.Code, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            TwoFactorOutcome.Succeeded => Ok(new { recoveryCodes = result.RecoveryCodes }),
            TwoFactorOutcome.RateLimited => TooManyRequests.Result(HttpContext, result.RetryAfter ?? TimeSpan.FromMinutes(1)),
            TwoFactorOutcome.NotApplicable => Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to confirm", detail: "Start two-factor setup first."),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The code was not accepted", detail: "The code is not valid. Check your authenticator app and try again."),
        };
    }

    /// <summary>Replaces all recovery codes with a fresh set, shown once. Needs a current authenticator code.</summary>
    [HttpPost("2fa/recovery-codes")]
    [Authorize]
    public async Task<IActionResult> RegenerateRecoveryCodes(TwoFactorCodeCommand command, CancellationToken cancellationToken)
    {
        var result = await twoFactor.RegenerateRecoveryCodesAsync(CurrentUserId(), command.Code, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            TwoFactorOutcome.Succeeded => Ok(new { recoveryCodes = result.RecoveryCodes }),
            TwoFactorOutcome.RateLimited => TooManyRequests.Result(HttpContext, result.RetryAfter ?? TimeSpan.FromMinutes(1)),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "The code was not accepted", detail: "The code is not valid. Check your authenticator app and try again."),
        };
    }

    // --- Password ----------------------------------------------------------------------------

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await passwordService.ChangePasswordAsync(CurrentUserId(), command, RemoteIp(), cancellationToken);

        if (result.Succeeded)
        {
            return NoContent();
        }

        if (result.RetryAfter is { } retryAfter)
        {
            return TooManyRequests.Result(HttpContext, retryAfter);
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

    // --- The member's own sessions -----------------------------------------------------------

    [HttpGet("sessions")]
    [Authorize]
    public async Task<IActionResult> ListSessions(CancellationToken cancellationToken)
    {
        var list = await sessions.ListAsync(CurrentUserId(), CurrentSessionId(), cancellationToken);

        return Ok(list.Select(session => new
        {
            id = session.Id,
            signedInAt = session.SignedInAt,
            lastActiveAt = session.LastActiveAt,
            ipAddress = session.IpAddress,
            userAgent = session.UserAgent,
            isCurrent = session.IsCurrent,
        }));
    }

    [HttpDelete("sessions/{id:guid}")]
    [Authorize]
    public async Task<IActionResult> EndOneSession(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await sessions.EndAsync(CurrentUserId(), id, RemoteIp(), cancellationToken);

        return outcome switch
        {
            EndSessionOutcome.Ended => NoContent(),
            EndSessionOutcome.Forbidden => Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: "You can only end your own sessions."),
            _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "There is no such session."),
        };
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Guid? CurrentSessionId() =>
        Guid.TryParse(User.FindFirstValue(AuthenticationClaims.SessionId), out var id) ? id : null;

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
