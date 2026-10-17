using System.Security.Claims;
using CaseRegister.Api.Contracts;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// The member's own profile and certificates. These use the plain "signed in with two-factor" policy
/// and none of the capability policies, which is why a member without a current practising
/// certificate can still reach them: it is how they get one.
/// </remarks>
[ApiController]
[Route("api/me")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
public sealed class MeController(IMemberProfileService profiles, IPractisingCertificateService certificates, IAccountSecurity security) : ControllerBase
{
    [HttpGet]
    [Authorize]
    [ProducesResponseType<ProfileResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(CurrentUserId(), cancellationToken);
        var summary = await security.GetAsync(CurrentUserId(), cancellationToken);

        return profile is null
            ? NotFound()
            : Ok(new ProfileResponse(
                profile.Id,
                profile.FullName,
                profile.Email,
                profile.MembershipNumber,
                profile.MembershipClass?.ToString(),
                profile.AgencyId is { } agencyId ? new AgencyReference(agencyId, profile.AgencyName ?? string.Empty, profile.AgencyActive) : null,
                profile.Roles,
                profile.HasCurrentPractisingCertificate,
                summary.LastSignIn is { } last ? new PreviousSignIn(last.At, last.Country, last.City) : null,
                summary.RecoveryCodesRemaining,
                summary.RecoveryCodesRemaining <= RecoveryCodeAdvice.WarnAtOrBelow));
    }

    [HttpGet("practising-certificates")]
    [Authorize]
    [ProducesResponseType<IReadOnlyList<CertificateInfo>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Certificates(CancellationToken cancellationToken) =>
        Ok(await certificates.ListAsync(CurrentUserId(), cancellationToken));

    /// <summary>Adds a new certificate to the member's history. It is not counted until an admin verifies it.</summary>
    [HttpPost("practising-certificates")]
    [Authorize]
    [ProducesResponseType<CreatedResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> AddCertificate(AddPractisingCertificateCommand command, CancellationToken cancellationToken)
    {
        var result = await certificates.AddAsync(CurrentUserId(), command, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);

        return result.Outcome switch
        {
            AddCertificateOutcome.Added => StatusCode(StatusCodes.Status201Created, new CreatedResponse(result.CertificateId!.Value)),
            AddCertificateOutcome.Duplicate => Problem(statusCode: StatusCodes.Status409Conflict, title: "Already recorded", detail: "A certificate with that number has already been recorded."),
            AddCertificateOutcome.NotAMember => Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: "Only members have practising certificates."),
            _ => BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "The certificate was not recorded",
                Detail = string.Join(' ', result.Errors ?? []),
            }),
        };
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
