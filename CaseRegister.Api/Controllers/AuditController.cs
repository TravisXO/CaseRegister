using System.Security.Claims;
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
public sealed class AuditController(IAuditReader reader, IAuditVerifier verifier) : ControllerBase
{
    /// <summary>One page of entries, newest first, filtered by date range, actor and action. Each call is itself audited.</summary>
    [HttpGet("entries")]
    [Authorize(Policy = nameof(Capability.ReadAuditLog))]
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
            ? Ok(new
            {
                entries = result.Page!.Entries.Select(entry => new
                {
                    sequence = entry.Sequence,
                    occurredAt = entry.OccurredAt,
                    actorType = entry.ActorType,
                    actorUserId = entry.ActorUserId,
                    actorRole = entry.ActorRole,
                    action = entry.Action,
                    targetType = entry.TargetType,
                    targetId = entry.TargetId,
                    agencyId = entry.AgencyId,
                    correlationId = entry.CorrelationId,
                    ipAddress = entry.IpAddress,
                    detail = entry.Detail,
                    before = entry.Before is null ? (object?)null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(entry.Before),
                    after = entry.After is null ? (object?)null : System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(entry.After),
                }),
                page = result.Page.Page,
                pageSize = result.Page.PageSize,
                total = result.Page.Total,
            })
            : Forbid();
    }

    /// <summary>Checks the chain and the anchors and reports the first broken link, if there is one. The check is itself audited.</summary>
    [HttpPost("verify")]
    [Authorize(Policy = nameof(Capability.VerifyAuditLog))]
    public async Task<IActionResult> Verify(VerifyAuditRequest request, CancellationToken cancellationToken)
    {
        var result = await verifier.VerifyAndRecordAsync(request.FromLastGoodAnchor, CurrentUserId(), RemoteIp(), cancellationToken);

        return Ok(new
        {
            intact = result.Intact,
            entriesChecked = result.EntriesChecked,
            headSequence = result.HeadSequence,
            anchorsChecked = result.AnchorsChecked,
            anchorStoreConfigured = result.AnchorStoreConfigured,
            fromAnchorSequence = result.FromAnchorSequence,
            verifiedAt = result.VerifiedAt,
            firstBreak = result.FirstBreak is null ? null : new { kind = result.FirstBreak.Kind.ToString(), sequence = result.FirstBreak.Sequence, message = result.FirstBreak.Message },
        });
    }

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private string? RemoteIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
