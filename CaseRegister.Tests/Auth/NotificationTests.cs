using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Notifications;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>The in-app notifications: who is told what, once, in words that reveal nothing about anyone else, and never at the cost of the action itself.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class NotificationTests(PostgresFixture postgres)
{
    private sealed record World(ApiFactory Factory, ApplicationUser A, ApplicationUser B, ApplicationUser C, Agency Agency, ApplicationUser Member, ApplicationUser Staff) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public Task<List<Notification>> For(ApplicationUser user) =>
            Factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientId == user.Id).OrderBy(n => n.CreatedAt).ToListAsync());

        public async Task<NotificationType[]> TypesFor(ApplicationUser user) => [.. (await For(user)).Select(n => n.Type)];

        public Task<int> All() => Factory.WithDbAsync(db => db.Notifications.CountAsync());
    }

    private async Task<World> WorldAsync(Action<IServiceCollection>? configureServices = null)
    {
        var factory = await ApiFactory.CreateAsync(postgres, configureServices: configureServices);
        var agency = await factory.CreateAgencyAsync();
        var admins = await factory.CreateAdminsAsync(3);
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        return new World(factory, admins[0], admins[1], admins[2], agency, member, staff);
    }

    private static readonly NotificationType[] _adminOnly =
    [
        NotificationType.ProposalWaiting, NotificationType.ProposalConfirmed, NotificationType.ProposalRejected, NotificationType.ProposalExpiring,
        NotificationType.ApplicationArrived, NotificationType.ImmediateRemoval, NotificationType.AdminCountLow,
    ];

    private static async Task<T> WithServiceAsync<T>(ApiFactory factory, Func<INotificationService, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();

        return await work(scope.ServiceProvider.GetRequiredService<INotificationService>());
    }

    // --- Each trigger tells the right people, and only them ---------------------------------------------------------

    [Fact]
    public async Task A_proposal_waits_for_every_other_admin_and_not_for_its_maker_or_anyone_who_is_not_an_admin()
    {
        await using var world = await WorldAsync();

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal([NotificationType.ProposalWaiting], await world.TypesFor(world.B));
        Assert.Equal([NotificationType.ProposalWaiting], await world.TypesFor(world.C));
        Assert.Empty(await world.For(world.A));
        Assert.Empty(await world.For(world.Member));
        Assert.Empty(await world.For(world.Staff));
        var told = (await world.For(world.B)).Single();
        Assert.Equal($"/admin/proposals/{proposal.Id}", told.Link);
    }

    [Fact]
    public async Task A_proposal_about_an_admin_does_not_wait_for_that_admin_because_they_cannot_decide_it()
    {
        await using var world = await WorldAsync();

        await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.B));

        Assert.Empty(await world.For(world.B));
        Assert.Equal([NotificationType.ProposalWaiting], await world.TypesFor(world.C));
    }

    [Fact]
    public async Task The_maker_is_told_when_their_proposal_is_confirmed_and_when_it_is_rejected_and_nothing_when_they_withdraw_it()
    {
        await using var world = await WorldAsync();

        var confirmed = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.B, confirmed);
        var rejected = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.EndSessions, world.B));
        Assert.Equal(DecisionOutcome.Done, (await world.Factory.RejectAsync(world.C, rejected)).Outcome);
        Assert.Equal([NotificationType.ProposalConfirmed, NotificationType.ProposalRejected], await world.TypesFor(world.A));
        Assert.Empty(await world.For(world.Member));
    }

    [Fact]
    public async Task Withdrawing_your_own_proposal_tells_nobody()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        var before = await world.All();

        Assert.Equal(DecisionOutcome.Done, (await world.Factory.WithdrawAsync(world.A, proposal)).Outcome);

        Assert.Equal(before, await world.All());
    }

    [Fact]
    public async Task An_application_tells_every_admin_that_one_arrived_and_nothing_about_who_and_nobody_else()
    {
        await using var world = await WorldAsync();

        var applicant = await SignUps.ApplyAsync(world.Factory, "jane.applicant@example.test");

        foreach (var admin in new[] { world.A, world.B, world.C })
        {
            var told = Assert.Single(await world.For(admin));
            Assert.Equal(NotificationType.ApplicationArrived, told.Type);
            Assert.Equal("/admin/applications", told.Link);
            Assert.DoesNotContain("jane", told.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("applicant", told.Text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(await world.For(world.Member));
        Assert.Empty(await world.For(applicant));
    }

    [Fact]
    public async Task An_immediate_removal_tells_the_other_admins_and_not_the_admin_who_did_it()
    {
        await using var world = await WorldAsync();

        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.A, world.Member)).Outcome);

        Assert.Equal([NotificationType.ImmediateRemoval], await world.TypesFor(world.B));
        Assert.Equal([NotificationType.ImmediateRemoval], await world.TypesFor(world.C));
        Assert.Empty(await world.For(world.A));
        Assert.Empty(await world.For(world.Member));
    }

    [Fact]
    public async Task Ending_sessions_is_exempt_from_the_cap_but_still_tells_the_other_admins()
    {
        await using var world = await WorldAsync();

        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.EndSessionsAsync(world.A, world.Member)).Outcome);

        Assert.Equal([NotificationType.ImmediateRemoval], await world.TypesFor(world.B));
        Assert.Contains("sessions", (await world.For(world.B)).Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Suspending_an_agency_tells_its_members_only_that_the_status_changed_and_the_other_admins_what_was_done()
    {
        await using var world = await WorldAsync();
        var other = await world.Factory.CreateAgencyAsync("Another Agency");
        var outsider = await world.Factory.CreateMemberAsync("outsider@example.test", other, [Role.RegisteredAgent]);

        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id)).Outcome);

        var told = Assert.Single(await world.For(world.Member));
        Assert.Equal(NotificationType.AgencyStatusChanged, told.Type);
        Assert.Equal("/profile", told.Link);
        Assert.DoesNotContain("suspend", told.Text, StringComparison.OrdinalIgnoreCase); // just that it changed, with a link
        Assert.Empty(await world.For(outsider));
        Assert.Equal([NotificationType.ImmediateRemoval], await world.TypesFor(world.B));
    }

    [Fact]
    public async Task Reactivating_an_agency_by_approval_tells_its_members_and_the_proposer()
    {
        await using var world = await WorldAsync();
        await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id);

        await world.Factory.ProposeAndConfirmAsync(world.B, world.C, Proposals.ForAgency(PendingActionType.ReactivateAgency, world.Agency.Id));

        Assert.Equal([NotificationType.AgencyStatusChanged, NotificationType.AgencyStatusChanged], await world.TypesFor(world.Member));
        Assert.Contains(NotificationType.ProposalConfirmed, await world.TypesFor(world.B));
    }

    [Fact]
    public async Task A_role_change_tells_the_person_whose_roles_changed_and_nobody_else_who_is_not_an_admin()
    {
        await using var world = await WorldAsync();

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal([NotificationType.RolesChanged], await world.TypesFor(world.Staff));
        Assert.Empty(await world.For(world.Member));
    }

    [Fact]
    public async Task A_two_factor_reset_tells_the_person_in_a_critical_notification_that_waits_for_their_acknowledgement()
    {
        await using var world = await WorldAsync();

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.Member));

        var told = Assert.Single(await world.For(world.Member), n => n.Type == NotificationType.TwoFactorResetOnYou);
        Assert.Equal(NotificationSeverity.Critical, told.Severity);
        Assert.True(told.RequiresAcknowledgement);
        Assert.Null(told.AcknowledgedAt);
    }

    [Fact]
    public async Task An_agent_never_receives_an_admin_notification_whatever_happens()
    {
        await using var world = await WorldAsync();

        await SignUps.ApplyAsync(world.Factory, "applicant@example.test");
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        await world.Factory.SuspendUserAsync(world.A, world.Staff);
        await world.Factory.EndSessionsAsync(world.A, world.Member);
        await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id);

        foreach (var person in new[] { world.Member, world.Staff })
        {
            Assert.DoesNotContain(await world.TypesFor(person), type => _adminOnly.Contains(type));
        }
    }

    // --- Once, whatever happens twice -------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_notification_is_made_once_however_often_it_is_asked_for()
    {
        await using var world = await WorldAsync();
        var item = NotificationFactory.ApplicationArrived(world.A.Id, world.Member.Id, "1");

        await WithServiceAsync(world.Factory, async service => { await service.NotifyAsync([item, item]); await service.NotifyAsync([item]); return 0; });

        Assert.Single(await world.For(world.A));
    }

    [Fact]
    public async Task A_new_window_is_a_new_notification_and_a_different_recipient_is_a_different_one()
    {
        await using var world = await WorldAsync();

        await WithServiceAsync(world.Factory, async service =>
        {
            await service.NotifyAsync([NotificationFactory.ApplicationArrived(world.A.Id, world.Member.Id, "1"), NotificationFactory.ApplicationArrived(world.A.Id, world.Member.Id, "2"), NotificationFactory.ApplicationArrived(world.B.Id, world.Member.Id, "1")]);
            return 0;
        });

        Assert.Equal(2, (await world.For(world.A)).Count);
        Assert.Single(await world.For(world.B));
    }

    [Fact]
    public async Task The_time_based_notifications_are_made_once_however_often_the_bell_is_fetched()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        world.Factory.Clock.Advance(TimeSpan.FromHours(50)); // 22 hours of 72 left

        for (var fetch = 0; fetch < 3; fetch++)
        {
            await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: false));
        }

        var told = Assert.Single(await world.For(world.A));
        Assert.Equal(NotificationType.ProposalExpiring, told.Type);
        Assert.Equal($"/admin/proposals/{proposal.Id}", told.Link);
    }

    [Fact]
    public async Task A_proposal_with_plenty_of_time_left_or_already_decided_raises_no_expiry_notice()
    {
        await using var world = await WorldAsync();
        var early = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        var decided = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.Member));
        await world.Factory.WithdrawAsync(world.A, decided);
        world.Factory.Clock.Advance(TimeSpan.FromHours(10));
        _ = early;

        await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: false));

        Assert.Empty(await world.For(world.A));
    }

    [Fact]
    public async Task Another_admin_does_not_get_the_proposers_expiry_notice()
    {
        await using var world = await WorldAsync();
        await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        world.Factory.Clock.Advance(TimeSpan.FromHours(50));

        await WithServiceAsync(world.Factory, service => service.GetAsync(world.B.Id, 20, unreadOnly: false));

        Assert.DoesNotContain(NotificationType.ProposalExpiring, await world.TypesFor(world.B));
    }

    // --- Certificates -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Moves the clock to a date in next year, so a test is the same whatever day it runs on. Members made after this have a certificate
    /// for that year.
    /// </summary>
    private static void TravelToNextYear(ApiFactory factory, int month, int day) =>
        factory.Clock.SetUtcNow(new DateTimeOffset(factory.Clock.GetUtcNow().Year + 1, month, day, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_certificate_that_expires_within_30_days_is_told_once_and_one_that_is_followed_by_next_years_is_not()
    {
        await using var world = await WorldAsync();
        TravelToNextYear(world.Factory, 12, 5);
        var expiring = await world.Factory.CreateMemberAsync("expiring@example.test", world.Agency, [Role.RegisteredAgent]);
        var followed = await world.Factory.CreateMemberAsync("followed@example.test", world.Agency, [Role.RegisteredAgent]);
        await world.Factory.AddCertificateAsync(followed, world.Factory.Today.Year + 1);

        await WithServiceAsync(world.Factory, service => service.GetAsync(expiring.Id, 20, unreadOnly: false));
        await WithServiceAsync(world.Factory, service => service.GetAsync(expiring.Id, 20, unreadOnly: false));
        await WithServiceAsync(world.Factory, service => service.GetAsync(followed.Id, 20, unreadOnly: false));

        var told = Assert.Single(await world.For(expiring));
        Assert.Equal(NotificationType.CertificateExpiring, told.Type);
        Assert.Equal("/profile", told.Link);
        Assert.Empty(await world.For(followed));
    }

    [Fact]
    public async Task A_certificate_with_plenty_of_time_left_is_not_told_about()
    {
        await using var world = await WorldAsync();
        TravelToNextYear(world.Factory, 3, 1);
        var member = await world.Factory.CreateMemberAsync("early@example.test", world.Agency, [Role.RegisteredAgent]);

        await WithServiceAsync(world.Factory, service => service.GetAsync(member.Id, 20, unreadOnly: false));

        Assert.Empty(await world.For(member));
    }

    [Fact]
    public async Task A_lapsed_certificate_is_told_once_for_a_member_who_had_one_and_never_for_someone_who_never_did()
    {
        await using var world = await WorldAsync();
        TravelToNextYear(world.Factory, 2, 1);
        var lapsed = await world.Factory.CreateMemberAsync("lapsed@example.test", world.Agency, [Role.RegisteredAgent], currentCertificate: false);
        await world.Factory.AddCertificateAsync(lapsed, world.Factory.Today.Year - 1);
        var never = await world.Factory.CreateMemberAsync("never@example.test", world.Agency, [Role.RegisteredAgent], currentCertificate: false);

        for (var fetch = 0; fetch < 2; fetch++)
        {
            await WithServiceAsync(world.Factory, service => service.GetAsync(lapsed.Id, 20, unreadOnly: false));
            await WithServiceAsync(world.Factory, service => service.GetAsync(never.Id, 20, unreadOnly: false));
        }

        Assert.Equal([NotificationType.CertificateLapsed], await world.TypesFor(lapsed));
        Assert.Empty(await world.For(never));
    }

    [Fact]
    public async Task Signing_in_makes_the_time_based_notifications_too_without_anyone_fetching_the_bell()
    {
        await using var world = await WorldAsync();
        TravelToNextYear(world.Factory, 2, 1);
        var lapsed = await world.Factory.CreateMemberAsync("lapsed@example.test", world.Agency, [Role.RegisteredAgent], currentCertificate: false);
        await world.Factory.AddCertificateAsync(lapsed, world.Factory.Today.Year - 1);

        await world.Factory.CreateApiClient().SignInWithTwoFactorAsync(world.Factory, lapsed);

        Assert.Equal([NotificationType.CertificateLapsed], await world.TypesFor(lapsed));
    }
    // --- Clearing away, and acknowledging ------------------------------------------------------------------------------

    private static NewNotification Make(Guid recipient, NotificationSeverity severity, string key) =>
        new(recipient, severity == NotificationSeverity.Critical ? NotificationType.BreakGlass : NotificationType.ApplicationArrived, severity, "Something happened.", "/profile", key);

    [Fact]
    public async Task Notifications_older_than_90_days_are_cleared_at_most_100_a_fetch_and_a_critical_one_never_before_it_is_acknowledged()
    {
        await using var world = await WorldAsync();
        var old = Enumerable.Range(0, 130).Select(index => Make(world.A.Id, NotificationSeverity.Info, $"old-{index}")).ToList();
        old.Add(Make(world.A.Id, NotificationSeverity.Critical, "old-critical"));
        old.Add(Make(world.A.Id, NotificationSeverity.Critical, "old-acknowledged"));
        await WithServiceAsync(world.Factory, async service => { await service.NotifyAsync(old); return 0; });
        var acknowledged = (await world.For(world.A)).Single(n => n.DedupeKey == "old-acknowledged");
        await WithServiceAsync(world.Factory, service => service.AcknowledgeAsync(world.A.Id, acknowledged.Id, null));

        world.Factory.Clock.Advance(TimeSpan.FromDays(100));
        await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 5, unreadOnly: false));

        // 130 + 2 old rows; 100 go in the first fetch, and neither critical one that is still open.
        Assert.Equal(132 - 100, (await world.For(world.A)).Count);
        await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 5, unreadOnly: false));
        var left = await world.For(world.A);
        Assert.Single(left); // the critical one that was never acknowledged stays, however old
        Assert.Equal("old-critical", left[0].DedupeKey);
    }

    [Fact]
    public async Task Recent_notifications_are_never_cleared()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service => { await service.NotifyAsync([Make(world.A.Id, NotificationSeverity.Info, "recent")]); return 0; });
        world.Factory.Clock.Advance(TimeSpan.FromDays(89));

        await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: false));

        Assert.Single(await world.For(world.A));
    }

    [Fact]
    public async Task Acknowledging_a_critical_notification_is_audited_once_and_only_by_the_person_it_was_made_for()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service => { await service.NotifyAsync([Make(world.A.Id, NotificationSeverity.Critical, "critical")]); return 0; });
        var critical = Assert.Single(await world.For(world.A));

        var byAnother = await WithServiceAsync(world.Factory, service => service.AcknowledgeAsync(world.B.Id, critical.Id, null));
        var first = await WithServiceAsync(world.Factory, service => service.AcknowledgeAsync(world.A.Id, critical.Id, "203.0.113.5"));
        var again = await WithServiceAsync(world.Factory, service => service.AcknowledgeAsync(world.A.Id, critical.Id, "203.0.113.5"));

        Assert.Equal(NotificationOutcome.NotFound, byAnother);
        Assert.Equal(NotificationOutcome.Done, first);
        Assert.Equal(NotificationOutcome.Done, again);
        var entries = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "NotificationAcknowledged").ToListAsync());
        var entry = Assert.Single(entries);
        Assert.Equal(world.A.Id, entry.ActorUserId);
        Assert.Equal(critical.Id.ToString(), entry.Subject);
        Assert.NotNull((await world.For(world.A)).Single().AcknowledgedAt);
    }

    [Fact]
    public async Task Only_a_critical_notification_is_acknowledged_and_reading_one_does_not_acknowledge_it()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service =>
        {
            await service.NotifyAsync([Make(world.A.Id, NotificationSeverity.Info, "info"), Make(world.A.Id, NotificationSeverity.Critical, "critical")]);
            return 0;
        });
        var info = (await world.For(world.A)).Single(n => n.DedupeKey == "info");
        var critical = (await world.For(world.A)).Single(n => n.DedupeKey == "critical");

        Assert.Equal(NotificationOutcome.NotCritical, await WithServiceAsync(world.Factory, service => service.AcknowledgeAsync(world.A.Id, info.Id, null)));
        await WithServiceAsync(world.Factory, service => service.MarkReadAsync(world.A.Id, critical.Id));

        var list = await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: false));
        Assert.Equal(2, list.UnreadCount);
        Assert.Equal(1, list.NeedsAcknowledgementCount);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "NotificationAcknowledged")));
    }

    [Fact]
    public async Task Reading_everything_leaves_the_critical_ones_waiting()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service =>
        {
            await service.NotifyAsync([Make(world.A.Id, NotificationSeverity.Info, "info"), Make(world.A.Id, NotificationSeverity.Warning, "warning"), Make(world.A.Id, NotificationSeverity.Critical, "critical")]);
            await service.MarkAllReadAsync(world.A.Id);
            return 0;
        });

        var list = await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: false));

        Assert.Equal(1, list.UnreadCount);
        var onlyOpen = await WithServiceAsync(world.Factory, service => service.GetAsync(world.A.Id, 20, unreadOnly: true));
        Assert.Equal("critical", (await world.For(world.A)).Single(n => n.Id == Assert.Single(onlyOpen.Items).Id).DedupeKey);
    }

    // --- Nothing is blocked by a notification --------------------------------------------------------------------------

    private sealed class BrokenNotifications : INotificationService
    {
        public Task NotifyAsync(IReadOnlyCollection<NewNotification> notifications, CancellationToken cancellationToken = default) => throw new InvalidOperationException("the notification store is down");

        public Task<NotificationList> GetAsync(Guid userId, int limit, bool unreadOnly, CancellationToken cancellationToken = default) => throw new InvalidOperationException("down");

        public Task EnsureTimeBasedAsync(Guid userId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("down");

        public Task<NotificationOutcome> MarkReadAsync(Guid userId, Guid id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("down");

        public Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("down");

        public Task<NotificationOutcome> AcknowledgeAsync(Guid userId, Guid id, string? remoteIpAddress, CancellationToken cancellationToken = default) => throw new InvalidOperationException("down");
    }

    [Fact]
    public async Task An_action_goes_ahead_even_when_the_notifications_cannot_be_made()
    {
        await using var world = await WorldAsync(services =>
        {
            services.AddScoped<INotificationService, BrokenNotifications>();
        });

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        var suspended = await world.Factory.SuspendUserAsync(world.A, world.Member);

        Assert.Equal([Role.CaseReviewer], await world.Factory.RolesOfAsync(world.Staff));
        Assert.Equal(AdminActionOutcome.Done, suspended.Outcome);
        Assert.Equal(0, await world.All());
    }

    [Fact]
    public async Task A_sign_up_goes_ahead_even_when_the_notifications_cannot_be_made()
    {
        await using var world = await WorldAsync(services => services.AddScoped<INotificationService, BrokenNotifications>());

        var applicant = await SignUps.ApplyAsync(world.Factory, "applicant@example.test");

        Assert.Equal(AccountStatus.Pending, applicant.AccountStatus);
    }

    // --- The words ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_notification_text_is_free_of_email_addresses_names_and_long_numbers()
    {
        var anyone = Guid.NewGuid();
        var place = new CaseRegister.Application.Auth.ClientLocation("ZM", "Lusaka");
        List<NewNotification> all =
        [
            .. Enum.GetValues<PendingActionType>().SelectMany(type => new[]
            {
                NotificationFactory.ProposalWaiting(anyone, anyone, type), NotificationFactory.ProposalConfirmed(anyone, anyone, type),
                NotificationFactory.ProposalRejected(anyone, anyone, type), NotificationFactory.ProposalExpiring(anyone, anyone, type),
            }),
            NotificationFactory.ApplicationArrived(anyone, anyone, "1"),
            NotificationFactory.ImmediateRemoval(anyone, anyone, "suspended an account"),
            NotificationFactory.LockoutForAdmin(anyone, anyone, TimeSpan.FromMinutes(15), place, wholeAccount: false),
            NotificationFactory.LockoutForAdmin(anyone, anyone, TimeSpan.FromHours(4), null, wholeAccount: true),
            NotificationFactory.LockoutForUser(anyone, anyone, TimeSpan.FromHours(1), place, wholeAccount: false),
            NotificationFactory.BreakGlassForAdmin(anyone, anyone, "a two-factor reset"),
            NotificationFactory.BreakGlassForTarget(anyone, anyone, "your two-factor was reset"),
            NotificationFactory.TwoFactorResetOnYou(anyone, anyone),
            NotificationFactory.CertificateExpiring(anyone, anyone, new DateOnly(2026, 12, 31)),
            NotificationFactory.CertificateLapsed(anyone, 2026),
            NotificationFactory.AgencyStatusChanged(anyone, anyone),
            NotificationFactory.RolesChanged(anyone, anyone),
            NotificationFactory.PasswordChanged(anyone, anyone, place),
            NotificationFactory.TwoFactorReset(anyone, anyone),
            NotificationFactory.RecoveryCodeUsed(anyone, anyone, place, 7),
            NotificationFactory.RecoveryCodesRegenerated(anyone, anyone, place),
            NotificationFactory.RecoveryCodesLow(anyone, 2, "w"),
            NotificationFactory.NewLocation(anyone, anyone, place),
            NotificationFactory.WrongSecondFactor(anyone, anyone, place),
        ];

        foreach (var item in all)
        {
            Assert.Empty(Notification.Check(item.Text, item.Link, item.DedupeKey));
            Assert.DoesNotContain('@', item.Text);
            Assert.DoesNotMatch(@"\d{5,}", item.Text); // no ID numbers, membership numbers or certificate numbers
            Assert.DoesNotMatch(@"[A-Z]{2,}[ -]?\d+", item.Text);
            Assert.StartsWith("/", item.Link, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("\\\\evil.example")]
    [InlineData("")]
    public void A_notification_can_only_link_to_a_path_inside_the_application(string link)
    {
        Assert.NotEmpty(Notification.Check("Text.", link, "key"));
    }

    // --- Where they come from, and no timers ------------------------------------------------------------------------------

    private static readonly string[] _notificationFolders = ["CaseRegister.Infrastructure/Notifications", "CaseRegister.Application/Notifications"];

    private static readonly string[] _forbiddenInTheBackground = ["Timer", "BackgroundService", "IHostedService", "Task.Delay", "Thread.Sleep", "Quartz", "Hangfire"];

    private static IEnumerable<string> NotificationSources() =>
        _notificationFolders
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(SolutionGraph.SolutionRoot, folder), "*.cs", SearchOption.AllDirectories));

    [Fact]
    public void Nothing_in_the_notification_code_runs_on_a_timer_or_in_the_background()
    {
        foreach (var file in NotificationSources())
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in _forbiddenInTheBackground)
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Who_is_told_comes_from_the_permission_matrix_and_no_role_is_named_in_the_notification_code()
    {
        foreach (var file in NotificationSources())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch(@"Role\.(Admin|RegisteredAgent|AgencyComplianceOfficer|CaseReviewer)\b", text);
        }
    }

    [Fact]
    public async Task The_people_told_about_something_are_the_ones_the_matrix_gives_that_capability_to()
    {
        await using var world = await WorldAsync();
        using var scope = world.Factory.Services.CreateScope();
        var recipients = scope.ServiceProvider.GetRequiredService<CaseRegister.Infrastructure.Notifications.NotificationRecipients>();

        var confirmers = await recipients.WithCapabilityAsync(CaseRegister.Application.Authorization.Capability.ConfirmAdminActions, []);
        var withoutA = await recipients.WithCapabilityAsync(CaseRegister.Application.Authorization.Capability.ConfirmAdminActions, [world.A.Id]);
        var alerts = await recipients.WithCapabilityAsync(CaseRegister.Application.Authorization.Capability.ReceiveSecurityAlerts, []);

        Assert.Equal(new[] { world.A.Id, world.B.Id, world.C.Id }.Order(), confirmers.Order());
        Assert.Equal(new[] { world.B.Id, world.C.Id }.Order(), withoutA.Order());
        Assert.Equal(confirmers.Order(), alerts.Order());
    }

    [Fact]
    public async Task A_suspended_admin_is_told_nothing()
    {
        await using var world = await WorldAsync();
        var extra = await world.Factory.CreateStaffAsync("admin4@example.test", Role.Admin);
        await world.Factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == extra.Id);
            user.AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });

        await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Empty(await world.For(extra));
    }

    // --- Over HTTP ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_endpoints_need_a_signed_in_user_and_show_each_person_only_their_own()
    {
        await using var world = await WorldAsync();
        await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var anonymous = world.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/notifications")).StatusCode);

        var b = world.Factory.CreateApiClient();
        await b.SignInWithTwoFactorAsync(world.Factory, world.B);
        var list = await (await b.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, list.GetProperty("unreadCount").GetInt32());
        Assert.Equal("ProposalWaiting", list.GetProperty("items")[0].GetProperty("type").GetString());

        var member = world.Factory.CreateApiClient();
        await member.SignInWithTwoFactorAsync(world.Factory, world.Member);
        var empty = await (await member.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, empty.GetProperty("items").GetArrayLength());

        // Somebody else's notification looks exactly like one that does not exist.
        var someone = list.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Post, $"/api/notifications/{someone}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Post, $"/api/notifications/{someone}/acknowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.SendAsync(HttpMethod.Post, $"/api/notifications/{Guid.NewGuid()}/read")).StatusCode);
    }

    [Fact]
    public async Task Reading_and_acknowledging_work_over_http_and_an_unsafe_call_needs_the_anti_forgery_token()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service =>
        {
            await service.NotifyAsync([Make(world.B.Id, NotificationSeverity.Info, "info"), Make(world.B.Id, NotificationSeverity.Critical, "critical")]);
            return 0;
        });
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.B);
        var items = (await (await client.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        var info = items.EnumerateArray().Single(item => item.GetProperty("severity").GetString() == "Info").GetProperty("id").GetGuid();
        var critical = items.EnumerateArray().Single(item => item.GetProperty("severity").GetString() == "Critical").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"/api/notifications/{info}/read", withCsrfToken: false)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(HttpMethod.Post, $"/api/notifications/{info}/acknowledge")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, $"/api/notifications/{info}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/notifications/read-all")).StatusCode);

        var afterRead = await (await client.GetAsync("/api/notifications")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, afterRead.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, afterRead.GetProperty("needsAcknowledgementCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, $"/api/notifications/{critical}/acknowledge")).StatusCode);
        var afterAck = await (await client.GetAsync("/api/notifications?unreadOnly=true")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterAck.GetProperty("unreadCount").GetInt32());
        Assert.Equal(0, afterAck.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_list_size_is_capped()
    {
        await using var world = await WorldAsync();
        await WithServiceAsync(world.Factory, async service =>
        {
            await service.NotifyAsync([.. Enumerable.Range(0, 120).Select(index => Make(world.B.Id, NotificationSeverity.Info, $"n-{index}"))]);
            return 0;
        });
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.B);

        var list = await (await client.GetAsync("/api/notifications?limit=100000")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(100, list.GetProperty("items").GetArrayLength());
        Assert.Equal(120, list.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, (await (await client.GetAsync("/api/notifications?limit=0")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetArrayLength());
    }

}
