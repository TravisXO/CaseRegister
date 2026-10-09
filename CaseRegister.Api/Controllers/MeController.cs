using System.Security.Claims;
using CaseRegister.Application.Accounts;
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
public sealed class MeController(IMemberProfileService profiles, IPractisingCertificateService certificates) : ControllerBase
{
    [HttpGet]
    [Authorize]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetAsync(CurrentUserId(), cancellationToken);

        return profile is null
            ? NotFound()
            : Ok(new
            {
                id = profile.Id,
                fullName = profile.FullName,
                email = profile.Email,
                membershipNumber = profile.MembershipNumber,
                membershipClass = profile.MembershipClass?.ToString(),
                agency = profile.AgencyId is null ? null : new { id = profile.AgencyId, name = profile.AgencyName },
                roles = profile.Roles,
                hasCurrentPractisingCertificate = profile.HasCurrentPractisingCertificate,
            });
    }

    [HttpGet("practising-certificates")]
    [Authorize]
    public async Task<IActionResult> Certificates(CancellationToken cancellationToken) =>
        Ok(await certificates.ListAsync(CurrentUserId(), cancellationToken));

    /// <summary>Adds a new certificate to the member's history. It is not counted until an admin verifies it.</summary>
    [HttpPost("practising-certificates")]
    [Authorize]
    public async Task<IActionResult> AddCertificate(AddPractisingCertificateCommand command, CancellationToken cancellationToken)
    {
        var result = await certificates.AddAsync(CurrentUserId(), command, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);

        return result.Outcome switch
        {
            AddCertificateOutcome.Added => StatusCode(StatusCodes.Status201Created, new { id = result.CertificateId }),
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
