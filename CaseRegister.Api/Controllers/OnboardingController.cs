using CaseRegister.Api.Contracts;
using CaseRegister.Api.Hardening;
using CaseRegister.Api.Security;
using CaseRegister.Application.Applications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// The two ways in that need no session: an agent applying, and an invited member of staff finishing setup.
/// Neither says anything about whether any account exists.
/// </remarks>
[ApiController]
[Route("api/auth")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
[RequestBodyLimit(RequestLimits.AuthBodyBytes)]
public sealed class OnboardingController(IMemberSignUpService signUp, IStaffSetupService staffSetup) : ControllerBase
{
    private const string TokenInvalidDetail = "This setup link is not valid. Ask an admin for a new invite.";

    /// <summary>
    /// An agent applies. Mistakes in the details are listed. Anything else gets the same answer every time:
    /// the application was submitted, if the details are valid.
    /// </summary>
    [HttpPost("sign-up")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.SignUpPerIp)]
    [ProducesResponseType<MessageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SignUp(SignUpCommand command, CancellationToken cancellationToken)
    {
        var result = await signUp.SignUpAsync(command, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            SignUpOutcome.Submitted => Ok(new MessageResponse(SignUpMessages.Submitted)),
            SignUpOutcome.Invalid => BadRequest(new ValidationProblemDetails(result.Errors!.ToDictionary(pair => pair.Key, pair => pair.Value))),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Verification failed", detail: "Verification failed. Please try again."),
        };
    }

    /// <summary>Step one of staff setup: the invited person asks for their two-factor key.</summary>
    [HttpPost("staff-setup/start")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.StaffSetupPerIp)]
    [ProducesResponseType<EnrolmentResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartSetup(StaffSetupStartCommand command, CancellationToken cancellationToken)
    {
        var result = await staffSetup.StartAsync(command.Token, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            StaffSetupOutcome.Succeeded => Ok(new EnrolmentResponse(result.Enrolment!.OtpAuthUri, result.Enrolment.SharedKey)),
            StaffSetupOutcome.RateLimited => TooManyRequests.Result(HttpContext, TimeSpan.FromMinutes(10)),
            _ => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Setup link not valid", detail: TokenInvalidDetail),
        };
    }

    /// <summary>Step two: the password and a code from the authenticator, together. This is all setup can do.</summary>
    [HttpPost("staff-setup/complete")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.StaffSetupPerIp)]
    [ProducesResponseType<RecoveryCodesResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CompleteSetup(StaffSetupCompleteCommand command, CancellationToken cancellationToken)
    {
        var result = await staffSetup.CompleteAsync(command.Token, command.Password, command.TwoFactorCode, RemoteIp(), cancellationToken);

        switch (result.Outcome)
        {
            case StaffSetupOutcome.Succeeded:
                return Ok(new RecoveryCodesResponse(result.RecoveryCodes!));
            case StaffSetupOutcome.RateLimited:
                return TooManyRequests.Result(HttpContext, TimeSpan.FromMinutes(10));
            case StaffSetupOutcome.InvalidPassword:
                var problem = new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "The password was not accepted", Detail = string.Join(' ', result.Errors!) };
                problem.Extensions["errors"] = result.Errors;
                return BadRequest(problem);
            case StaffSetupOutcome.InvalidCode:
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "The code was not accepted", detail: "The code is not valid. Check your authenticator app and try again.");
            case StaffSetupOutcome.NoEnrolmentStarted:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to confirm", detail: "Start two-factor setup first.");
            default:
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Setup link not valid", detail: TokenInvalidDetail);
        }
    }

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
