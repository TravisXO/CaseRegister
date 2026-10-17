using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CaseRegister.Tests.Auth;

/// <summary>Security notices ("was this you?"), sessions that end when something changes, the last sign-in, the cap on immediate removals and the read entries.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SecurityNoticeTests(PostgresFixture postgres)
{
    /// <summary>Stands in for the proxy: the place the next request is said to come from.</summary>
    private sealed class SettableLocation : IClientLocationAccessor
    {
        public ClientLocation? Current { get; set; }
    }

    private sealed record World(ApiFactory Factory, SettableLocation Place, ApplicationUser A, ApplicationUser B, ApplicationUser C, Agency Agency, ApplicationUser Person) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            var response = await client.SignInWithTwoFactorAsync(Factory, user);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return client;
        }

        public Task<List<Notification>> NoticesFor(ApplicationUser user, NotificationType type) =>
            Factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientId == user.Id && n.Type == type).ToListAsync());

        public Task<int> SessionsOf(ApplicationUser user) => Factory.WithDbAsync(db => db.Sessions.CountAsync(s => s.UserId == user.Id));

        public Task<int> Entries(string action) => Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == action));
    }

    private async Task<World> WorldAsync()
    {
        var place = new SettableLocation();
        var factory = await ApiFactory.CreateAsync(postgres, configureServices: services => services.Replace(ServiceDescriptor.Singleton<IClientLocationAccessor>(place)));
        var agency = await factory.CreateAgencyAsync();
        var admins = await factory.CreateAdminsAsync(3);
        var person = await factory.CreateMemberAsync("person@example.test", agency, [Role.RegisteredAgent]);

        return new World(factory, place, admins[0], admins[1], admins[2], agency, person);
    }

    private static ClientLocation Lusaka => new("ZM", "Lusaka");

    private static ClientLocation Ndola => new("ZM", "Ndola");

    // --- Other sessions end, at once, when something about the account changes ---------------------------------------

    [Fact]
    public async Task Changing_the_password_ends_every_other_session_at_once_and_this_one_carries_on()
    {
        await using var world = await WorldAsync();
        var here = await world.SignInAsync(world.Person);
        var elsewhere = await world.SignInAsync(world.Person);
        Assert.Equal(2, await world.SessionsOf(world.Person));
        var stamp = (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp;

        var changed = await here.ChangePasswordAsync(ApiFactory.Password, "a different long password 42", world.Factory.NextTotpCode(world.Person));

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(1, await world.SessionsOf(world.Person)); // deleted now, not on the next stamp check
        Assert.NotEqual(stamp, (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/me")).StatusCode);

        // The session that carried on still knows which one it is (the list marks it as the current one).
        var list = await (await here.GetAsync("/api/auth/sessions")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(Assert.Single(list.EnumerateArray()).GetProperty("isCurrent").GetBoolean());
    }

    [Fact]
    public async Task Making_new_recovery_codes_ends_every_other_session_rotates_the_stamp_and_revokes_the_old_codes()
    {
        await using var world = await WorldAsync();
        var here = await world.SignInAsync(world.Person);
        var elsewhere = await world.SignInAsync(world.Person);
        var stamp = (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp;

        var response = await here.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = world.Factory.NextTotpCode(world.Person) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await world.SessionsOf(world.Person));
        Assert.NotEqual(stamp, (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, (await here.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/me")).StatusCode);
        Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityRecoveryCodesRegenerated));
    }

    [Fact]
    public async Task A_role_change_ends_all_the_persons_sessions_and_rotates_their_stamp()
    {
        await using var world = await WorldAsync();
        var session = await world.SignInAsync(world.Person);
        var stamp = (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp;

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.AssignRole(world.Person, Role.AgencyComplianceOfficer));

        Assert.Equal(0, await world.SessionsOf(world.Person));
        Assert.NotEqual(stamp, (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.GetAsync("/api/me")).StatusCode);
    }

    // --- Notices ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_password_change_tells_the_person_and_nobody_else_and_names_no_one()
    {
        await using var world = await WorldAsync();
        world.Place.Current = Lusaka;
        var client = await world.SignInAsync(world.Person);

        await client.ChangePasswordAsync(ApiFactory.Password, "a different long password 42", world.Factory.NextTotpCode(world.Person));

        var notice = Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityPasswordChanged));
        Assert.Contains("Lusaka, ZM", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("person@example.test", notice.Text, StringComparison.Ordinal);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.SecurityPasswordChanged && n.RecipientId != world.Person.Id)));
    }

    [Fact]
    public async Task A_recovery_code_used_to_sign_in_is_a_notice_with_the_number_left_and_a_warning_at_two_or_fewer()
    {
        await using var world = await WorldAsync();
        var codes = await RecoveryCodesOfAsync(world, world.Person);

        for (var used = 0; used < codes.Count - 2; used++)
        {
            var client = world.Factory.CreateApiClient();
            await client.SignInAsync(world.Person.Email!);
            Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(codes[used])).StatusCode);
        }

        // Ten codes, eight used: two left.
        var notices = await world.NoticesFor(world.Person, NotificationType.SecurityRecoveryCodesUsed);
        Assert.Equal(codes.Count - 2, notices.Count);
        Assert.Contains("You have 2 left", notices.Last().Text, StringComparison.Ordinal);
        var low = await world.NoticesFor(world.Person, NotificationType.SecurityRecoveryCodesLow);
        Assert.Single(low);
        Assert.Contains("2 recovery codes left", low.Single().Text, StringComparison.Ordinal);
    }

    private static async Task<List<string>> RecoveryCodesOfAsync(World world, ApplicationUser user)
    {
        var client = await world.SignInAsync(user);
        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = world.Factory.NextTotpCode(user) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await TwoFactorFlows.RecoveryCodesAsync(response);
    }

    [Fact]
    public async Task A_right_password_followed_by_a_wrong_code_is_one_notice_for_each_ten_minutes()
    {
        await using var world = await WorldAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);

        await client.VerifyTwoFactorAsync("000000");
        await client.VerifyTwoFactorAsync("111111");
        await client.VerifyTwoFactorAsync("222222");

        var notice = Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityWrongSecondFactor));
        Assert.Contains("change it", notice.Text, StringComparison.Ordinal);

        // Later it is told again. (The clock only moves forward, so the window is passed by moving it on.)
        world.Factory.Clock.Advance(TimeSpan.FromMinutes(11));
        var later = world.Factory.CreateApiClient();
        await later.SignInAsync(world.Person.Email!);
        await later.VerifyTwoFactorAsync("333333");
        Assert.Equal(2, (await world.NoticesFor(world.Person, NotificationType.SecurityWrongSecondFactor)).Count);

        // A wrong password alone is not this notice.
        var none = world.Factory.CreateApiClient();
        await none.SignInAsync(world.Person.Email!, "not the password");
        Assert.Equal(2, (await world.NoticesFor(world.Person, NotificationType.SecurityWrongSecondFactor)).Count);
    }

    // --- The session tells the page when it ends ---------------------------------------------------------------------------

    [Fact]
    public async Task The_session_says_when_the_eight_hour_limit_falls_and_how_long_idle_lasts()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Person);
        world.Factory.Clock.Advance(TimeSpan.FromMinutes(20)); // activity does not move the hard limit

        var session = await (await client.GetAsync("/api/auth/session")).Content.ReadFromJsonAsync<JsonElement>();

        var ends = session.GetProperty("sessionEndsAt").GetDateTimeOffset();
        Assert.InRange((ends - (world.Factory.Clock.GetUtcNow() - TimeSpan.FromMinutes(20) + TimeSpan.FromHours(8))).TotalSeconds, -5, 5);
        Assert.Equal(1800, session.GetProperty("idleTimeoutSeconds").GetInt32());
    }

    // --- Where from ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sign_in_from_a_new_country_and_city_pair_is_a_notice_and_a_seen_one_is_not()
    {
        await using var world = await WorldAsync();
        world.Place.Current = Lusaka;
        await world.SignInAsync(world.Person); // the first sign-in has nothing to be new against
        Assert.Empty(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));

        await world.SignInAsync(world.Person); // same place again
        Assert.Empty(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));

        world.Place.Current = Ndola;
        await world.SignInAsync(world.Person);
        var notice = Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));
        Assert.Contains("Ndola, ZM", notice.Text, StringComparison.Ordinal);

        // Ndola is now seen; so a second one from there says nothing more.
        await world.SignInAsync(world.Person);
        Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));
    }

    [Fact]
    public async Task A_place_not_seen_for_90_days_is_new_again()
    {
        await using var world = await WorldAsync();
        world.Place.Current = Lusaka;
        await world.SignInAsync(world.Person);
        world.Place.Current = Ndola;
        await world.SignInAsync(world.Person);
        Assert.Single(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));

        world.Factory.Clock.Advance(TimeSpan.FromDays(91));
        world.Place.Current = Lusaka;
        await world.SignInAsync(world.Person);

        Assert.Equal(2, (await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation)).Count);
    }

    [Fact]
    public async Task Raw_addresses_are_never_compared_or_kept_in_the_history()
    {
        await using var world = await WorldAsync();
        world.Place.Current = Lusaka;

        foreach (var address in new[] { "203.0.113.5", "198.51.100.9", "192.0.2.77" })
        {
            var client = world.Factory.CreateApiClient();
            await client.SignInAsync(world.Person.Email!, forwardedFor: address);
            await client.VerifyTwoFactorAsync(world.Factory.NextTotpCode(world.Person), address);
        }

        // Three addresses, one place: no notice, and the history holds places and times only.
        Assert.Empty(await world.NoticesFor(world.Person, NotificationType.SecurityNewLocation));
        var history = await world.Factory.WithDbAsync(db => db.SignInHistory.AsNoTracking().Where(h => h.UserId == world.Person.Id).ToListAsync());
        Assert.Equal(3, history.Count);
        Assert.All(history, entry => Assert.Equal(("ZM", "Lusaka"), (entry.Country, entry.City)));
        Assert.DoesNotContain(typeof(CaseRegister.Infrastructure.Persistence.SignInHistoryEntry).GetProperties(), property => property.Name.Contains("Ip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_profile_shows_the_sign_in_before_this_one_as_a_place_and_a_time_and_never_an_address()
    {
        await using var world = await WorldAsync();
        var first = await world.SignInAsync(world.Person);
        Assert.Equal(JsonValueKind.Null, (await ProfileAsync(first)).GetProperty("lastSignIn").ValueKind); // nothing before the first

        world.Place.Current = Ndola;
        world.Factory.Clock.Advance(TimeSpan.FromHours(3));
        world.Place.Current = Lusaka;
        var second = await world.SignInAsync(world.Person);

        var last = (await ProfileAsync(second)).GetProperty("lastSignIn");
        Assert.Equal(JsonValueKind.Object, last.ValueKind);
        Assert.Equal(["at", "city", "country"], last.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private static async Task<JsonElement> ProfileAsync(ApiClient client)
    {
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_profile_counts_the_recovery_codes_left_and_warns_at_two_or_fewer()
    {
        await using var world = await WorldAsync();
        var codes = await RecoveryCodesOfAsync(world, world.Person);
        var client = await world.SignInAsync(world.Person);

        var plenty = await ProfileAsync(client);
        Assert.Equal(codes.Count, plenty.GetProperty("recoveryCodesRemaining").GetInt32());
        Assert.False(plenty.GetProperty("recoveryCodesLow").GetBoolean());

        await world.Factory.WithDbAsync(async db =>
        {
            foreach (var code in await db.RecoveryCodes.Where(c => c.UserId == world.Person.Id && c.RevokedAt == null).Take(codes.Count - 2).ToListAsync())
            {
                code.UsedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync();

            return 0;
        });

        var few = await ProfileAsync(client);
        Assert.Equal(2, few.GetProperty("recoveryCodesRemaining").GetInt32());
        Assert.True(few.GetProperty("recoveryCodesLow").GetBoolean());
    }

    // --- The cap on immediate removals ---------------------------------------------------------------------------------

    [Fact]
    public async Task One_admin_gets_ten_immediate_removals_in_a_rolling_day_then_it_has_to_be_proposed()
    {
        await using var world = await WorldAsync();
        var members = new List<ApplicationUser>();
        for (var index = 0; index < 12; index++)
        {
            members.Add(await world.Factory.CreateMemberAsync($"member{index}@example.test", world.Agency, [Role.RegisteredAgent]));
        }

        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.A, members[index])).Outcome);
        }

        // The eleventh is not done at once...
        var over = await world.Factory.SuspendUserAsync(world.A, members[10]);
        Assert.Equal(AdminActionOutcome.LimitReached, over.Outcome);
        Assert.Equal(AccountStatus.Approved, (await world.Factory.GetUserAsync(members[10].Email!)).AccountStatus);

        // ...it can be proposed instead, which a second admin confirms.
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.SuspendUser, members[10]));
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        Assert.Equal(AccountStatus.Suspended, (await world.Factory.GetUserAsync(members[10].Email!)).AccountStatus);

        // Ending sessions is exempt, and another admin has a count of their own.
        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.EndSessionsAsync(world.A, members[11])).Outcome);
        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.B, members[11])).Outcome);
    }

    [Fact]
    public async Task Below_the_cap_a_proposal_for_a_plain_suspension_is_still_not_needed_and_the_cap_rolls_off_after_a_day()
    {
        await using var world = await WorldAsync();
        var members = new List<ApplicationUser>();
        for (var index = 0; index < 12; index++)
        {
            members.Add(await world.Factory.CreateMemberAsync($"member{index}@example.test", world.Agency, [Role.RegisteredAgent]));
        }

        Assert.Equal(ProposeOutcome.NoApprovalNeeded, (await world.Factory.ProposeAsync(world.A, Proposals.ForUser(PendingActionType.SuspendUser, members[0]))).Outcome);

        for (var index = 0; index < 10; index++)
        {
            await world.Factory.SuspendUserAsync(world.A, members[index]);
        }

        Assert.Equal(AdminActionOutcome.LimitReached, (await world.Factory.SuspendUserAsync(world.A, members[10])).Outcome);

        world.Factory.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.A, members[10])).Outcome);
    }

    // --- Read entries -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Looking_at_an_admin_list_is_one_entry_for_each_person_list_and_filter_in_ten_minutes()
    {
        await using var world = await WorldAsync();
        var a = await world.SignInAsync(world.A);
        var b = await world.SignInAsync(world.B);
        var before = await world.Entries("ListViewed");

        for (var round = 0; round < 5; round++)
        {
            await a.GetAsync("/api/admin/pending-actions");
        }

        Assert.Equal(before + 1, await world.Entries("ListViewed")); // a reload is not a new view

        await a.GetAsync("/api/admin/pending-actions?includeClosed=true");
        await a.GetAsync("/api/admin/applications");
        await a.GetAsync("/api/admin/admins");
        await a.GetAsync("/api/admin/people?search=person");
        await b.GetAsync("/api/admin/pending-actions");
        Assert.Equal(before + 6, await world.Entries("ListViewed")); // another filter, another list, another person

        world.Factory.Clock.Advance(TimeSpan.FromMinutes(11));
        await a.GetAsync("/api/admin/pending-actions");
        Assert.Equal(before + 7, await world.Entries("ListViewed")); // and again after ten minutes

        var entry = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "ListViewed" && e.Subject == "people").SingleAsync());
        Assert.Equal(world.A.Id, entry.ActorUserId);
        Assert.Contains("search=person", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_view_that_was_refused_is_not_written_and_a_marker_can_only_be_claimed_once()
    {
        await using var world = await WorldAsync();
        var member = await world.SignInAsync(world.Person);
        var before = await world.Entries("ListViewed");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/pending-actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/admin/people?search=person")).StatusCode);
        Assert.Equal(before, await world.Entries("ListViewed"));

        // Two requests at the same moment: the unique key lets exactly one of them write the entry.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var scope = world.Factory.Services.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<CaseRegister.Application.Auditing.IAuditReadLog>().ClaimAsync(world.A.Id, "proposals", "x");
        }));
        Assert.Equal(1, results.Count(claimed => claimed));
    }

    [Fact]
    public async Task Reading_the_audit_log_is_one_entry_for_each_filter_in_ten_minutes_and_paging_is_the_same_view()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.A);
        var before = await world.Entries("AuditLogRead");

        await admin.GetAsync("/api/audit/entries");
        await admin.GetAsync("/api/audit/entries?page=2");
        await admin.GetAsync("/api/audit/entries?page=3&pageSize=10");
        Assert.Equal(before + 1, await world.Entries("AuditLogRead"));

        await admin.GetAsync("/api/audit/entries?action=RecordViewed");
        Assert.Equal(before + 2, await world.Entries("AuditLogRead"));
    }

    // --- Finding people -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_finds_accounts_by_name_or_email_and_sees_only_what_is_needed_to_tell_them_apart()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.A);

        var short_ = await (await admin.GetAsync("/api/admin/people?search=pe")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, short_.GetArrayLength());

        var found = await (await admin.GetAsync("/api/admin/people?search=PERSON@exam")).Content.ReadFromJsonAsync<JsonElement>();
        var person = Assert.Single(found.EnumerateArray());
        Assert.Equal(world.Person.Id, person.GetProperty("id").GetGuid());
        Assert.Equal(["email", "fullName", "id", "roles", "status"], person.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        // The search is for the text typed, not a pattern.
        var wildcard = await (await admin.GetAsync("/api/admin/people?search=%25%25%25")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, wildcard.GetArrayLength());
    }
}
