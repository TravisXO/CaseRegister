using System.Security.Claims;
using System.Text.Json;
using CaseRegister.Api.Contracts;
using CaseRegister.Api.Security.Authorization;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Api.Controllers;

/// <param name="FromLastGoodAnchor">Start after the newest anchor that still matches, instead of from the first entry.</param>
public sealed record VerifyAuditRequest(bool FromLastGoodAnchor = false);

/// <remarks>
/// Reading the audit log and checking it. No rule is decided here: the reader decides who sees what (an admin
/// everything, a compliance officer only their own agency, without IP addresses), and both calls write their own
/// audit entry.
/// </remarks>
[ApiController]
[Route("api/audit")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
public sealed class AuditController(IAuditReader reader, IAuditVerifier verifier) : ControllerBase
{
    /// <summary>One page of entries, newest first, filtered by date range, actor and action. Each call is itself audited.</summary>
    [HttpGet("entries")]
    [Authorize(Policy = nameof(Capability.ReadAuditLog))]
    [ProducesResponseType<AuditPageResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Entries(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] Guid? actor,
        [FromQuery] string? action,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var viewer = new AuditViewer(CurrentUserId(), UserClaims.Roles(User), UserClaims.AgencyId(User));
        var result = await reader.QueryAsync(new AuditQuery(from, to, actor, action, page, pageSize), viewer, RemoteIp(), cancellationToken);

        return result.Outcome == AuditReadOutcome.Ok
            ? Ok(new AuditPageResponse(
                [.. result.Page!.Entries.Select(entry => new AuditEntryResponse(
                    entry.Sequence,
                    entry.OccurredAt,
                    entry.ActorType,
                    entry.ActorUserId,
                    entry.ActorRole,
                    entry.Action,
                    entry.TargetType,
                    entry.TargetId,
                    entry.AgencyId,
                    entry.CorrelationId,
                    entry.IpAddress,
                    entry.Detail,
                    entry.Before is null ? null : JsonSerializer.Deserialize<JsonElement>(entry.Before),
                    entry.After is null ? null : JsonSerializer.Deserialize<JsonElement>(entry.After),
                    entry.ActorName,
                    entry.TargetName))],
                result.Page.Page,
                result.Page.PageSize,
                result.Page.Total))
            : Forbid();
    }

    /// <summary>Checks the chain and the anchors and reports the first broken link, if there is one. The check is itself audited.</summary>
    [HttpPost("verify")]
    [Authorize(Policy = nameof(Capability.VerifyAuditLog))]
    [ProducesResponseType<AuditVerification>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(VerifyAuditRequest request, CancellationToken cancellationToken)
    {
        var result = await verifier.VerifyAndRecordAsync(request.FromLastGoodAnchor, CurrentUserId(), RemoteIp(), cancellationToken);

        return Ok(result);
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
