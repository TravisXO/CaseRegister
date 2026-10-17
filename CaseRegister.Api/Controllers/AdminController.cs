using System.Security.Claims;
using System.Text.Json;
using CaseRegister.Api.Contracts;
using CaseRegister.Api.Security;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Approvals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// Admin actions on accounts and agencies. No business rule is here: the approval engine and the
/// administration service decide everything, and this only maps their answers to HTTP. Each endpoint
/// has the named policy for its capability, so only admins with completed two-factor reach any of it.
/// </remarks>
[ApiController]
[Route("api/admin")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
public sealed class AdminController(IPendingActionService approvals, IAccountAdministrationService administration, IApplicationReviewService applications, IAdminDirectory directory) : ControllerBase
{
    /// <summary>Who the admins are, and whether there are enough of them: the list warns while there are fewer than three active ones.</summary>
    [HttpGet("admins")]
    [Authorize(Policy = nameof(Capability.ViewPendingActions))]
    [AuditListView("admins")]
    [ProducesResponseType<AdminListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Admins(CancellationToken cancellationToken)
    {
        var listing = await directory.ListAdminsAsync(cancellationToken);

        return Ok(new AdminListResponse(
            [.. listing.Admins.Select(admin => new AdminSummaryResponse(admin.UserId, admin.FullName, admin.IsActive, admin.TwoFactorOn))],
            listing.ActiveCount,
            listing.Minimum,
            listing.Recommended,
            listing.ActiveCount < listing.Recommended));
    }

    /// <summary>Finds accounts by name or email (three characters at least), so an admin can name the right one. Each search is audited.</summary>
    [HttpGet("people")]
    [Authorize(Policy = nameof(Capability.ViewPendingActions))]
    [AuditListView("people")]
    [ProducesResponseType<IReadOnlyList<PersonResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> People([FromQuery] string? search, CancellationToken cancellationToken) =>
        Ok((await directory.FindPeopleAsync(search ?? string.Empty, cancellationToken))
            .Select(person => new PersonResponse(person.UserId, person.FullName, person.Email, person.Status, person.Roles)));

    // --- Proposals that need a second admin ----------------------------------------------------

    /// <summary>Proposals still waiting, with their expiry worked out. <c>includeClosed</c> adds decided and lapsed ones.</summary>
    [HttpGet("pending-actions")]
    [Authorize(Policy = nameof(Capability.ViewPendingActions))]
    [AuditListView("proposals")]
    [ProducesResponseType<IReadOnlyList<PendingActionResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool includeClosed, CancellationToken cancellationToken) =>
        Ok((await approvals.ListAsync(includeClosed, cancellationToken)).Select(Shape));

    [HttpGet("pending-actions/{id:guid}")]
    [Authorize(Policy = nameof(Capability.ViewPendingActions))]
    [ProducesResponseType<PendingActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken) =>
        await approvals.GetAsync(id, cancellationToken) is { } view ? Ok(Shape(view)) : NotFound();

    [HttpPost("pending-actions")]
    [Authorize(Policy = nameof(Capability.ProposeAdminActions))]
    [ProducesResponseType<PendingActionResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Propose(ProposeActionCommand command, CancellationToken cancellationToken)
    {
        var result = await approvals.ProposeAsync(CurrentUserId(), command, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            ProposeOutcome.Proposed => CreatedAtAction(nameof(Get), new { id = result.Action!.Id }, Shape(result.Action)),
            ProposeOutcome.TargetNotFound => NotFound(),
            ProposeOutcome.AlreadyPending => Fail(StatusCodes.Status409Conflict, "Already proposed", "There is already a pending proposal of this kind for this target."),
            ProposeOutcome.NoApprovalNeeded => Fail(StatusCodes.Status400BadRequest, "No approval needed", "This takes access away from someone who is not an admin, so it is done at once, not proposed."),
            ProposeOutcome.Refused when IsSelf(result.Problems) => Fail(StatusCodes.Status403Forbidden, "Not allowed", "You cannot propose an action that targets yourself.", result.Problems),
            ProposeOutcome.Refused => Fail(StatusCodes.Status422UnprocessableEntity, "Not possible now", "A rule does not allow this in the current situation.", result.Problems),
            ProposeOutcome.NotAnAdmin => Forbid(),
            _ => Fail(StatusCodes.Status400BadRequest, "The proposal was not made", "Check the type, the target and the reason.", result.Problems),
        };
    }

    /// <summary>Confirms a proposal made by someone else. Needs the hash of what was shown and a fresh two-factor code.</summary>
    [HttpPost("pending-actions/{id:guid}/confirm")]
    [Authorize(Policy = nameof(Capability.ConfirmAdminActions))]
    [ProducesResponseType<PendingActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Confirm(Guid id, ConfirmActionCommand command, CancellationToken cancellationToken)
    {
        var result = await approvals.ConfirmAsync(id, CurrentUserId(), command, RemoteIp(), cancellationToken);

        return result.Outcome switch
        {
            ConfirmOutcome.Confirmed => Ok(ShapeWithLink(result)),
            ConfirmOutcome.NotFound => NotFound(),
            ConfirmOutcome.HashMismatch => Fail(StatusCodes.Status409Conflict, "Not what you approved", "This proposal is not the one you were shown. Read it again before confirming."),
            ConfirmOutcome.NotPending => Fail(StatusCodes.Status409Conflict, "Already decided", "This proposal has already been decided.", result.Problems),
            ConfirmOutcome.Expired => Fail(StatusCodes.Status409Conflict, "Expired", "This proposal has expired.", result.Problems),
            ConfirmOutcome.NotAllowed => Fail(StatusCodes.Status403Forbidden, "Not allowed", "You cannot confirm this proposal.", result.Problems),
            ConfirmOutcome.Refused => Fail(StatusCodes.Status422UnprocessableEntity, "Not possible now", "A rule no longer allows this. Nothing was done and the proposal is still pending.", result.Problems),
            ConfirmOutcome.CodeRejected => Fail(StatusCodes.Status401Unauthorized, "Verification failed", "Verification failed. Check the code and try again."),
            ConfirmOutcome.RateLimited => TooManyRequests.Result(HttpContext, result.RetryAfter ?? TimeSpan.FromMinutes(1)),
            _ => Forbid(),
        };
    }

    /// <summary>Turns down a proposal made by someone else.</summary>
    [HttpPost("pending-actions/{id:guid}/reject")]
    [Authorize(Policy = nameof(Capability.ConfirmAdminActions))]
    [ProducesResponseType<PendingActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(Guid id, DecisionReasonCommand command, CancellationToken cancellationToken) =>
        Decision(await approvals.RejectAsync(id, CurrentUserId(), command.Reason, RemoteIp(), cancellationToken));

    /// <summary>Takes back the caller's own pending proposal.</summary>
    [HttpPost("pending-actions/{id:guid}/withdraw")]
    [Authorize(Policy = nameof(Capability.ProposeAdminActions))]
    [ProducesResponseType<PendingActionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Withdraw(Guid id, DecisionReasonCommand command, CancellationToken cancellationToken) =>
        Decision(await approvals.WithdrawAsync(id, CurrentUserId(), command.Reason, RemoteIp(), cancellationToken));

    // --- Applications from would-be members ---------------------------------------------------------

    /// <summary>
    /// Applications waiting for a decision, with their flags and suggested agencies. To approve or reject one, propose
    /// ApproveApplication or RejectApplication for the applicant: a different admin confirms.
    /// </summary>
    [HttpGet("applications")]
    [Authorize(Policy = nameof(Capability.ViewApplications))]
    [AuditListView("applications")]
    [ProducesResponseType<IReadOnlyList<ApplicationView>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Applications(CancellationToken cancellationToken) =>
        Ok(await applications.ListPendingAsync(cancellationToken));

    // --- Actions that take access away, at once --------------------------------------------------

    [HttpPost("users/{id:guid}/suspend")]
    [Authorize(Policy = nameof(Capability.RestrictAccess))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SuspendUser(Guid id, DecisionReasonCommand command, CancellationToken cancellationToken) =>
        Immediate(await administration.SuspendUserAsync(CurrentUserId(), id, command.Reason, RemoteIp(), cancellationToken));

    [HttpPost("users/{id:guid}/end-sessions")]
    [Authorize(Policy = nameof(Capability.RestrictAccess))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EndSessions(Guid id, DecisionReasonCommand command, CancellationToken cancellationToken) =>
        Immediate(await administration.EndSessionsAsync(CurrentUserId(), id, command.Reason, RemoteIp(), cancellationToken));

    [HttpPost("agencies/{id:guid}/suspend")]
    [Authorize(Policy = nameof(Capability.RestrictAccess))]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SuspendAgency(Guid id, DecisionReasonCommand command, CancellationToken cancellationToken) =>
        Immediate(await administration.SuspendAgencyAsync(CurrentUserId(), id, command.Reason, RemoteIp(), cancellationToken));

    // --- Mapping -----------------------------------------------------------------------------------

    private IActionResult Decision(DecisionResult result) => result.Outcome switch
    {
        DecisionOutcome.Done => Ok(Shape(result.Action!)),
        DecisionOutcome.NotFound => NotFound(),
        DecisionOutcome.NotPending => Fail(StatusCodes.Status409Conflict, "Already decided", "This proposal has already been decided.", result.Problems),
        DecisionOutcome.Expired => Fail(StatusCodes.Status409Conflict, "Expired", "This proposal has expired.", result.Problems),
        DecisionOutcome.NotAllowed => Fail(StatusCodes.Status403Forbidden, "Not allowed", "You cannot decide this proposal.", result.Problems),
        DecisionOutcome.Invalid => Fail(StatusCodes.Status400BadRequest, "A reason is needed", "Give a reason of 10 to 500 characters."),
        _ => Forbid(),
    };

    private IActionResult Immediate(AdminActionResult result) => result.Outcome switch
    {
        AdminActionOutcome.Done => NoContent(),
        AdminActionOutcome.NotFound => NotFound(),
        AdminActionOutcome.Invalid => Fail(StatusCodes.Status400BadRequest, "A reason is needed", "Give a reason of 10 to 500 characters."),
        AdminActionOutcome.NotAllowed => Fail(StatusCodes.Status403Forbidden, "Not allowed", "You cannot act on yourself.", result.Problems),
        AdminActionOutcome.NeedsTwoAdmins => Fail(StatusCodes.Status403Forbidden, "Needs a second admin", "The target is an admin, so this has to be proposed and confirmed by a different admin.", result.Problems),
        AdminActionOutcome.LimitReached => Fail(StatusCodes.Status422UnprocessableEntity, "Limit reached", "You have used your immediate removals for now. Propose this instead; a second admin confirms it.", result.Problems),
        AdminActionOutcome.Refused => Fail(StatusCodes.Status422UnprocessableEntity, "Not possible now", "A rule does not allow this in the current situation.", result.Problems),
        _ => Forbid(),
    };

    private static ObjectResult Fail(int status, string title, string detail, IReadOnlyList<ApprovalViolation>? problems = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        if (problems is { Count: > 0 })
        {
            problem.Extensions["problems"] = problems.Select(violation => new { code = violation.Code, message = violation.Message });
        }

        return new ObjectResult(problem) { StatusCode = status };
    }

    private static bool IsSelf(IReadOnlyList<ApprovalViolation>? problems) =>
        problems?.Any(problem => problem.Code == ApprovalRules.SelfTarget) == true;

    /// <summary>The confirmation as shown to the confirming admin. A staff invite's setup link, or the enrolment code of a two-factor reset, is in it once, here and nowhere else.</summary>
    private static PendingActionResponse ShapeWithLink(ConfirmResult result) => Shape(result.Action!) with { SetupLink = result.SetupLink, EnrolmentCode = result.EnrolmentCode };

    private static PendingActionResponse Shape(PendingActionView view) => new(
        view.Id,
        view.Type,
        new ProposalTarget(view.TargetUserId, view.TargetAgencyId, view.TargetEmail, view.TargetName),
        view.Description,
        JsonSerializer.Deserialize<JsonElement>(view.Payload),
        view.PayloadHash,
        view.Reason,
        view.Status,
        new ProposalPerson(view.ProposedByUserId, view.ProposedByName),
        view.ProposedAt,
        view.ExpiresAt,
        view.DecidedByUserId,
        view.DecidedAt,
        view.DecisionReason,
        view.LastRefusedAt is null ? null : new ProposalRefusal(view.LastRefusalCode, view.LastRefusalMessage, view.LastRefusedAt));

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
