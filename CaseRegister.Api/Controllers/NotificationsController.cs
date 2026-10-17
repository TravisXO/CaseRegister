using System.Security.Claims;
using CaseRegister.Api.Contracts;
using CaseRegister.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CaseRegister.Api.Controllers;

/// <remarks>
/// A person's own notifications and nobody else's: every call is about the signed-in user, and a notification belonging to somebody else
/// is answered exactly like one that does not exist. These use the plain "signed in with two-factor" policy and no capability, because
/// every role has an inbox. Nothing here polls: the page asks when the person navigates or acts.
/// </remarks>
[ApiController]
[Route("api/notifications")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
public sealed class NotificationsController(INotificationService notifications) : ControllerBase
{
    /// <summary>The most recent notifications and the counts. Fetching also makes the time-based ones that are due and clears away old ones.</summary>
    [HttpGet]
    [Authorize]
    [ProducesResponseType<NotificationListResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int limit = 20, [FromQuery] bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        var list = await notifications.GetAsync(CurrentUserId(), limit, unreadOnly, cancellationToken);

        return Ok(new NotificationListResponse(
            list.UnreadCount,
            list.NeedsAcknowledgementCount,
            [.. list.Items.Select(item => new NotificationResponse(item.Id, item.Type, item.Severity, item.Text, item.Link, item.CreatedAt, item.ReadAt, item.AcknowledgedAt, item.NeedsAcknowledgement))]));
    }

    [HttpPost("{id:guid}/read")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken) =>
        await notifications.MarkReadAsync(CurrentUserId(), id, cancellationToken) == NotificationOutcome.Done ? NoContent() : NotFound();

    /// <summary>Marks everything read except critical notifications, which wait to be acknowledged.</summary>
    [HttpPost("read-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        await notifications.MarkAllReadAsync(CurrentUserId(), cancellationToken);

        return NoContent();
    }

    /// <summary>Acknowledges a critical notification. The acknowledgement is recorded in the audit log.</summary>
    [HttpPost("{id:guid}/acknowledge")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken cancellationToken) =>
        await notifications.AcknowledgeAsync(CurrentUserId(), id, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken) switch
        {
            NotificationOutcome.Done => NoContent(),
            NotificationOutcome.NotCritical => Problem(statusCode: StatusCodes.Status409Conflict, title: "Nothing to acknowledge", detail: "Only a critical notification is acknowledged."),
            _ => NotFound(),
        };

    private Guid CurrentUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
