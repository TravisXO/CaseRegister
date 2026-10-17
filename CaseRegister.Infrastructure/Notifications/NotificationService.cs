using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Authorization;
using CaseRegister.Application.Notifications;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Notifications;

/// <summary>
/// Notifications. They are made in a unit of work of their own, after whatever caused them has been committed, one insert at a time
/// with INSERT ... ON CONFLICT DO NOTHING on the dedupe key: so a repeat (a retried request, a second sign-in the same minute) makes nothing new,
/// a failure cannot undo the business action, and a notification can never make the action fail.
/// </summary>
internal sealed partial class NotificationService(
    IServiceScopeFactory scopes,
    AppDbContext db,
    TimeBasedNotifications timeBased,
    IAuditLog audit,
    TimeProvider time,
    ILogger<NotificationService> logger) : INotificationService
{
    /// <summary>The most old rows removed by one fetch of the bell.</summary>
    public const int CleanupBatch = 100;

    public const int MaxListSize = 100;

    public async Task NotifyAsync(IReadOnlyCollection<NewNotification> notifications, CancellationToken cancellationToken = default)
    {
        if (notifications.Count == 0)
        {
            return;
        }

        try
        {
            // A scope, a connection and a context of their own: nothing here can touch the caller's transaction or its change tracker.
            using var scope = scopes.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = time.GetUtcNow();

            foreach (var item in notifications)
            {
                if (Notification.Check(item.Text, item.Link, item.DedupeKey) is { Count: > 0 } problems)
                {
                    LogRejected(logger, item.Type, string.Join(' ', problems));
                    continue;
                }

                try
                {
                    var id = Guid.CreateVersion7();
                    await context.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                        INSERT INTO notifications (id, created_at, updated_at, recipient_id, type, severity, text, link, dedupe_key)
                        VALUES ({id}, {now}, {now}, {item.RecipientId}, {item.Type.ToString()}, {item.Severity.ToString()}, {item.Text}, {item.Link}, {item.DedupeKey})
                        ON CONFLICT (dedupe_key) DO NOTHING
                        """,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // One notification failing (for example a recipient who no longer exists) does not stop the others.
                    LogFailed(logger, item.Type, exception);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, notifications.First().Type, exception);
        }
    }

    public async Task EnsureTimeBasedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        try
        {
            await NotifyAsync([.. await timeBased.DueAsync(userId, cancellationToken)], cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTimeBasedFailed(logger, exception);
        }
    }

    public async Task<NotificationList> GetAsync(Guid userId, int limit, bool unreadOnly, CancellationToken cancellationToken = default)
    {
        await EnsureTimeBasedAsync(userId, cancellationToken);
        await ClearOldAsync(cancellationToken);

        var size = Math.Clamp(limit, 1, MaxListSize);
        var mine = db.Notifications.AsNoTracking().Where(notification => notification.RecipientId == userId);

        // Open means unread, or for a critical one not yet acknowledged.
        var open = mine.Where(notification => notification.Severity == NotificationSeverity.Critical ? notification.AcknowledgedAt == null : notification.ReadAt == null);

        var unread = await open.CountAsync(cancellationToken);
        var needing = await mine.CountAsync(notification => notification.Severity == NotificationSeverity.Critical && notification.AcknowledgedAt == null, cancellationToken);

        var rows = await (unreadOnly ? open : mine)
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Take(size)
            .ToListAsync(cancellationToken);

        return new NotificationList(
            unread,
            needing,
            [.. rows.Select(notification => new NotificationView(
                notification.Id,
                notification.Type,
                notification.Severity,
                notification.Text,
                notification.Link,
                notification.CreatedAt,
                notification.ReadAt,
                notification.AcknowledgedAt,
                notification.RequiresAcknowledgement))]);
    }

    /// <summary>
    /// Removes notifications older than 90 days, at most <see cref="CleanupBatch"/> a time. A critical notification that has not been
    /// acknowledged is never removed, however old. (There is no scheduler: this runs when somebody fetches their bell.)
    /// </summary>
    private async Task ClearOldAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cutoff = time.GetUtcNow() - Notification.Retention;
            var old = await db.Notifications.AsNoTracking()
                .Where(notification => notification.CreatedAt < cutoff
                    && (notification.Severity != NotificationSeverity.Critical || notification.AcknowledgedAt != null))
                .OrderBy(notification => notification.CreatedAt)
                .Select(notification => notification.Id)
                .Take(CleanupBatch)
                .ToListAsync(cancellationToken);

            if (old.Count > 0)
            {
                await db.Notifications.Where(notification => old.Contains(notification.Id)).ExecuteDeleteAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCleanupFailed(logger, exception);
        }
    }

    public async Task<NotificationOutcome> MarkReadAsync(Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.RecipientId == userId, cancellationToken);
        if (notification is null)
        {
            return NotificationOutcome.NotFound;
        }

        notification.MarkRead(time.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // read at the same moment from another tab; the result is the same
        }

        return NotificationOutcome.Done;
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();

        await db.Notifications
            .Where(notification => notification.RecipientId == userId && notification.ReadAt == null && notification.Severity != NotificationSeverity.Critical)
            .ExecuteUpdateAsync(set => set.SetProperty(notification => notification.ReadAt, now).SetProperty(notification => notification.UpdatedAt, now), cancellationToken);
    }

    public async Task<NotificationOutcome> AcknowledgeAsync(Guid userId, Guid id, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == id && n.RecipientId == userId, cancellationToken);
        if (notification is null)
        {
            return NotificationOutcome.NotFound;
        }

        if (!notification.RequiresAcknowledgement)
        {
            return NotificationOutcome.NotCritical;
        }

        if (notification.AcknowledgedAt is not null)
        {
            return NotificationOutcome.Done; // already acknowledged: nothing to do, and nothing more to audit
        }

        notification.Acknowledge(time.GetUtcNow());

        try
        {
            // The acknowledgement and the entry that records it are one transaction.
            await Transactions.RunAsync(
                db,
                async () =>
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.WriteAsync(
                        new AuditEntry(
                            AuditAction.NotificationAcknowledged,
                            userId,
                            Subject: notification.Id.ToString(),
                            IpAddress: remoteIpAddress,
                            Detail: $"acknowledged a critical notification: {notification.Type}",
                            TargetType: "Notification"),
                        cancellationToken);
                },
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear(); // acknowledged at the same moment from another tab
        }

        return NotificationOutcome.Done;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Type} notification was not made: {Problems}")]
    private static partial void LogRejected(ILogger logger, NotificationType type, string problems);

    [LoggerMessage(Level = LogLevel.Error, Message = "A {Type} notification could not be made. The action that caused it was not affected.")]
    private static partial void LogFailed(ILogger logger, NotificationType type, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The time-based notifications could not be made. Nothing else was affected.")]
    private static partial void LogTimeBasedFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Old notifications could not be cleared. Nothing else was affected.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}

/// <summary>
/// Who is told what is worked out from the permission matrix: the roles that hold a capability, never a role named in the code that
/// sends the notification. So when the matrix changes, so does who hears.
/// </summary>
internal sealed class NotificationRecipients(AppDbContext db)
{
    /// <summary>Approved accounts that hold a role the matrix gives <paramref name="capability"/> to, except the ones named.</summary>
    public async Task<IReadOnlyList<Guid>> WithCapabilityAsync(Capability capability, IReadOnlyCollection<Guid> except, CancellationToken cancellationToken = default)
    {
        var roleIds = PermissionMatrix.RolesFor(capability).Keys.Select(RoleSeed.IdOf).ToList();

        return await (from userRole in db.UserRoles
                      join user in db.Users on userRole.UserId equals user.Id
                      where roleIds.Contains(userRole.RoleId) && user.AccountStatus == AccountStatus.Approved && !except.Contains(user.Id)
                      select user.Id).Distinct().ToListAsync(cancellationToken);
    }

    /// <summary>The approved members of an agency.</summary>
    public async Task<IReadOnlyList<Guid>> AgencyMembersAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .Where(user => user.AgencyId == agencyId && user.AccountStatus == AccountStatus.Approved)
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// The notifications that depend on the date and not on anything anyone did: a proposal of yours about to run out, and a practising
/// certificate about to expire or already lapsed. There is no scheduler, so they are made when a person signs in and when they fetch the
/// bell. The dedupe key makes each of them happen once.
/// </summary>
internal sealed class TimeBasedNotifications(AppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan ProposalWarning = TimeSpan.FromHours(24);
    public const int CertificateWarningDays = 30;

    public async Task<IReadOnlyList<NewNotification>> DueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = MembershipCalendar.Today(time);
        List<NewNotification> due = [];

        var roles = await RolesOfAsync(userId, cancellationToken);

        // A proposal of yours that nobody has decided, with less than a day left.
        if (PermissionMatrix.IsAllowed(Capability.ProposeAdminActions, roles))
        {
            var horizon = now + ProposalWarning;
            var expiring = await db.PendingActions.AsNoTracking()
                .Where(action => action.ProposedByUserId == userId && action.Status == PendingActionStatus.Pending && action.ExpiresAt > now && action.ExpiresAt <= horizon)
                .Select(action => new { action.Id, action.Type })
                .ToListAsync(cancellationToken);
            due.AddRange(expiring.Select(action => NotificationFactory.ProposalExpiring(userId, action.Id, action.Type)));
        }

        // An agent's certificate: about to expire with nothing to follow it, or lapsed.
        if (roles.Any(role => role.IsAgentRole()))
        {
            var verified = await db.PractisingCertificates.AsNoTracking()
                .Where(certificate => certificate.UserId == userId && certificate.VerifiedAt != null)
                .ToListAsync(cancellationToken);
            var current = verified.Where(certificate => certificate.IsCurrent(today)).ToList();

            foreach (var certificate in current.Where(c => c.ValidTo.DayNumber - today.DayNumber <= CertificateWarningDays))
            {
                var followed = verified.Any(other => other.Id != certificate.Id && other.ValidFrom <= certificate.ValidTo.AddDays(1) && other.ValidTo > certificate.ValidTo);
                if (!followed)
                {
                    due.Add(NotificationFactory.CertificateExpiring(userId, certificate.Id, certificate.ValidTo));
                }
            }

            if (current.Count == 0 && verified.Count > 0)
            {
                due.Add(NotificationFactory.CertificateLapsed(userId, today.Year));
            }
        }

        return due;
    }

    private async Task<IReadOnlyList<Role>> RolesOfAsync(Guid userId, CancellationToken cancellationToken)
    {
        var held = await db.UserRoles.AsNoTracking().Where(userRole => userRole.UserId == userId).Select(userRole => userRole.RoleId).ToListAsync(cancellationToken);

        return [.. Enum.GetValues<Role>().Where(role => held.Contains(RoleSeed.IdOf(role)))];
    }
}
