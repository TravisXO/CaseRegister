using System.Globalization;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Authorization;
using CaseRegister.Application.Notifications;
using CaseRegister.Domain.Approvals;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Notifications;

/// <summary>
/// Something an admin operation did that somebody should be told about. The operation runs inside a transaction that can still roll back
/// or be retried, so it only records the fact here; the notification is made after the commit, by <see cref="NotificationDispatcher"/>.
/// </summary>
internal abstract record NotificationIntent(Guid EventId);

internal sealed record RolesChangedIntent(Guid EventId, Guid UserId) : NotificationIntent(EventId);

internal sealed record AgencyStatusChangedIntent(Guid EventId, Guid AgencyId) : NotificationIntent(EventId);

internal sealed record TwoFactorResetIntent(Guid EventId, Guid UserId) : NotificationIntent(EventId);

/// <summary>
/// Turns what happened into notifications for the right people, after the work has been committed. The recipients come from
/// <see cref="NotificationRecipients"/> (the permission matrix); the words come from <see cref="NotificationFactory"/>. Nothing here can
/// fail the action that caused it: whatever goes wrong is logged and swallowed.
/// </summary>
internal sealed partial class NotificationDispatcher(INotificationService notifications, NotificationRecipients recipients, ILogger<NotificationDispatcher> logger)
{
    /// <summary>A proposal was made: every admin who can confirm it, except the one who made it and anyone it is about, is told it is waiting.</summary>
    public Task ProposalMadeAsync(Guid proposalId, PendingActionType type, Guid proposerId, Guid? targetUserId, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            HashSet<Guid> except = [proposerId];
            if (targetUserId is { } target)
            {
                except.Add(target); // they cannot decide something about themselves, so it is not waiting for them
            }

            var admins = await recipients.WithCapabilityAsync(Capability.ConfirmAdminActions, except, cancellationToken);
            await notifications.NotifyAsync([.. admins.Select(admin => NotificationFactory.ProposalWaiting(admin, proposalId, type))], cancellationToken);
        });

    public Task ProposalConfirmedAsync(Guid proposalId, PendingActionType type, Guid proposerId, IEnumerable<NotificationIntent> intents, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            List<NewNotification> items = [NotificationFactory.ProposalConfirmed(proposerId, proposalId, type)];
            items.AddRange(await IntentsAsync(intents, cancellationToken));
            await notifications.NotifyAsync(items, cancellationToken);
        });

    public Task ProposalRejectedAsync(Guid proposalId, PendingActionType type, Guid proposerId, CancellationToken cancellationToken = default) =>
        SafeAsync(() => notifications.NotifyAsync([NotificationFactory.ProposalRejected(proposerId, proposalId, type)], cancellationToken));

    /// <summary>An application arrived (or came back): the admins who review applications are told. Nothing about who applied.</summary>
    public Task ApplicationArrivedAsync(Guid applicantId, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            var reviewers = await recipients.WithCapabilityAsync(Capability.ViewApplications, [], cancellationToken);

            // The applicant's id is the subject and the moment is the window, so an application that comes back later is told again.
            var window = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            await notifications.NotifyAsync([.. reviewers.Select(admin => NotificationFactory.ApplicationArrived(admin, applicantId, window))], cancellationToken);
        });

    /// <summary>One admin took access away at once (or ended sessions): the other admins are told, and so is anyone the action touched.</summary>
    public Task ImmediateRemovalAsync(Guid actorId, string what, IEnumerable<NotificationIntent> intents, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            var others = await recipients.WithCapabilityAsync(Capability.RestrictAccess, [actorId], cancellationToken);
            var eventId = Guid.CreateVersion7();

            List<NewNotification> items = [.. others.Select(admin => NotificationFactory.ImmediateRemoval(admin, eventId, what))];
            items.AddRange(await IntentsAsync(intents, cancellationToken));
            await notifications.NotifyAsync(items, cancellationToken);
        });

    /// <summary>An address (or a whole account) was locked out of signing in: the person is told, and so are the admins who receive security alerts.</summary>
    public Task LockoutAsync(Guid userId, Guid lockoutId, TimeSpan duration, ClientLocation? place, bool wholeAccount, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            var admins = await recipients.WithCapabilityAsync(Capability.ReceiveSecurityAlerts, [userId], cancellationToken);

            List<NewNotification> items = [NotificationFactory.LockoutForUser(userId, lockoutId, duration, place, wholeAccount)];
            items.AddRange(admins.Select(admin => NotificationFactory.LockoutForAdmin(admin, lockoutId, duration, place, wholeAccount)));
            await notifications.NotifyAsync(items, cancellationToken);
        });

    /// <summary>A break-glass command was run: every admin who receives security alerts is told, and so is the person it was run on. All critical.</summary>
    public Task BreakGlassAsync(Guid targetUserId, string command, CancellationToken cancellationToken = default) =>
        SafeAsync(async () =>
        {
            var admins = await recipients.WithCapabilityAsync(Capability.ReceiveSecurityAlerts, [targetUserId], cancellationToken);
            var eventId = Guid.CreateVersion7();

            List<NewNotification> items = [NotificationFactory.BreakGlassForTarget(targetUserId, eventId, command)];
            items.AddRange(admins.Select(admin => NotificationFactory.BreakGlassForAdmin(admin, eventId, command)));
            await notifications.NotifyAsync(items, cancellationToken);
        });

    /// <summary>Admins who should hear about a security event (a lockout, a break-glass action), as the permission matrix says.</summary>
    public Task<IReadOnlyList<Guid>> SecurityAlertRecipientsAsync(CancellationToken cancellationToken = default) =>
        recipients.WithCapabilityAsync(Capability.ReceiveSecurityAlerts, [], cancellationToken);

    public async Task<IReadOnlyList<NewNotification>> IntentsAsync(IEnumerable<NotificationIntent> intents, CancellationToken cancellationToken = default)
    {
        List<NewNotification> items = [];

        foreach (var intent in intents)
        {
            switch (intent)
            {
                case RolesChangedIntent roles:
                    items.Add(NotificationFactory.RolesChanged(roles.UserId, roles.EventId));
                    break;

                case TwoFactorResetIntent reset:
                    items.Add(NotificationFactory.TwoFactorResetOnYou(reset.UserId, reset.EventId));
                    break;

                case AgencyStatusChangedIntent agency:
                    items.AddRange((await recipients.AgencyMembersAsync(agency.AgencyId, cancellationToken)).Select(member => NotificationFactory.AgencyStatusChanged(member, agency.EventId)));
                    break;
            }
        }

        return items;
    }

    /// <summary>Runs the work and swallows any failure: a notification is never a reason for an action to fail.</summary>
    private async Task SafeAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Telling people about an action failed. The action itself was not affected.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
