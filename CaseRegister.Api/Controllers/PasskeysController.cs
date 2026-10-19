using System.Security.Claims;
using CaseRegister.Api.Contracts;
using CaseRegister.Api.Hardening;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// Passkeys: signing in with one, and the person's own list of them. Every endpoint carries the sign-in rate limit. Adding, removing and the
/// step-up for other sensitive acts all need a fresh proof (an authenticator or recovery code, or a passkey answer), and an anti-forgery token
/// like every state-changing request. The authenticator app keeps working whatever is done to the passkeys.
/// </remarks>
[ApiController]
[Route("api/auth")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[RequestBodyLimit(RequestLimits.AuthBodyBytes)]
[EnableRateLimiting(RateLimitPolicies.SignInPerIp)]
public sealed class PasskeysController(IPasskeyService passkeys) : ControllerBase
{
    private const string SignInFailedDetail = "Sign-in failed. Try again, or use your password and authenticator app.";
    private const string StepUpFailedDetail = "That was not accepted. Check the code or use your passkey again.";

    // --- Signing in ---------------------------------------------------------------------------------------------------

    /// <summary>The options for signing in with a passkey. Nobody is named, so this reveals nothing about any account.</summary>
    [HttpPost("passkey/sign-in/options")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SignInOptions(CancellationToken cancellationToken)
    {
        var options = await passkeys.BeginSignInAsync(cancellationToken);

        return Options(options);
    }

    /// <summary>Signs in with a passkey that verified the user. That is a password and a second factor in one.</summary>
    [HttpPost("passkey/sign-in")]
    [AllowAnonymous]
    [ProducesResponseType<StatusResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SignIn(PasskeySignInCommand command, CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteSignInAsync(command.Credential, RemoteIp(), cancellationToken);

        return result.Outcome == PasskeyOutcome.Succeeded
            ? Ok(new StatusResponse("signed-in"))
            : Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Sign-in failed", detail: SignInFailedDetail);
    }

    // --- The person's own passkeys -------------------------------------------------------------------------------------

    [HttpGet("passkeys")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<PasskeyView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await passkeys.ListAsync(CurrentUserId(), cancellationToken));

    /// <summary>The options for adding a passkey. The answer must come back to <c>POST /api/auth/passkeys</c> within five minutes.</summary>
    [HttpPost("passkeys/options")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> AddOptions(CancellationToken cancellationToken)
    {
        var options = await passkeys.BeginRegistrationAsync(CurrentUserId(), cancellationToken);

        return options is null
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "A passkey cannot be added", detail: "You can have up to 10 passkeys. Remove one first.")
            : Options(options);
    }

    /// <summary>Adds the passkey the browser made, after a fresh authenticator code or passkey answer.</summary>
    [HttpPost("passkeys")]
    [Authorize]
    [ProducesResponseType<PasskeyView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Add(AddPasskeyCommand command, CancellationToken cancellationToken)
    {
        var result = await passkeys.CompleteRegistrationAsync(CurrentUserId(), command.Credential, command.Name, command.StepUp, RemoteIp(), cancellationToken);

        return Answer(result, () => Ok(result.Passkey), "The passkey was not added", "The passkey could not be verified. It has to confirm that it is you (a PIN, a fingerprint or a face). Try again.");
    }

    [HttpPatch("passkeys/{id}")]
    [Authorize]
    [ProducesResponseType<PasskeyView>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(string id, RenamePasskeyCommand command, CancellationToken cancellationToken)
    {
        var result = await passkeys.RenameAsync(CurrentUserId(), id, command.Name, RemoteIp(), cancellationToken);

        return Answer(result, () => Ok(result.Passkey), "The passkey was not renamed", "The passkey could not be renamed.");
    }

    /// <summary>Removes one passkey, after a fresh authenticator code or passkey answer. Your other sessions end.</summary>
    [HttpPost("passkeys/{id}/remove")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(string id, RemovePasskeyCommand command, CancellationToken cancellationToken)
    {
        var result = await passkeys.RemoveAsync(CurrentUserId(), id, command.StepUp, RemoteIp(), cancellationToken);

        return Answer(result, NoContent, "The passkey was not removed", "The passkey could not be removed.");
    }

    /// <summary>The options for proving it is you with a passkey, instead of a code, before a sensitive act.</summary>
    [HttpPost("passkeys/step-up/options")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> StepUpOptions(CancellationToken cancellationToken)
    {
        var options = await passkeys.BeginStepUpAsync(CurrentUserId(), cancellationToken);

        return options is null
            ? Problem(statusCode: StatusCodes.Status409Conflict, title: "No passkey", detail: "You have no passkey to use. Use a code from your authenticator app.")
            : Options(options);
    }

    private ContentResult Options(PasskeyOptionsResult options)
    {
        // The options are JSON the browser reads (parseCreationOptionsFromJSON / parseRequestOptionsFromJSON), and must never be cached.
        Response.Headers.CacheControl = "no-store";

        return Content(options.OptionsJson, "application/json");
    }

    private IActionResult Answer(PasskeyResult result, Func<IActionResult> success, string title, string failureDetail) => result.Outcome switch
    {
        PasskeyOutcome.Succeeded => success(),
        PasskeyOutcome.RateLimited => TooManyRequests.Result(HttpContext, result.RetryAfter ?? TimeSpan.FromMinutes(1)),
        PasskeyOutcome.StepUpFailed => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not confirmed", detail: StepUpFailedDetail),
        PasskeyOutcome.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: "There is no such passkey."),
        _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: title, detail: failureDetail),
    };

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
