using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

/// <summary>
/// Lockout that cannot be used against an admin: wrong passwords are counted per account and per address, a guesser's address is locked
/// out and nobody else's, the whole account only when wrong passwords come from three places, and nobody's status ever changes.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class SignInThrottleTests(PostgresFixture postgres)
{
    private const string Email = "admin1@example.test";
    private const string Attacker = "198.51.100.10";
    private const string Owner = "203.0.113.20";

    private static async Task WrongPasswordsAsync(ApiFactory factory, string email, string address, int count)
    {
        var client = factory.CreateApiClient();
        for (var attempt = 0; attempt < count; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(email, "wrong password " + attempt, forwardedFor: address)).StatusCode);
        }
    }

    private static async Task<HttpStatusCode> RightPasswordAsync(ApiFactory factory, string email, string address) =>
        (await factory.CreateApiClient().SignInAsync(email, forwardedFor: address)).StatusCode;

    private static Task<List<SignInLockout>> LockoutsAsync(ApiFactory factory) =>
        factory.WithDbAsync(db => db.SignInLockouts.AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync());

    private static Task<int> OpenFailuresAsync(ApiFactory factory, FailureKind kind) =>
        factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.Kind == kind && f.ClearedAt == null));

    // --- One address cannot lock everyone else out ---------------------------------------------------------------------

    [Fact]
    public async Task Five_wrong_passwords_from_one_address_lock_that_address_out_of_that_account_and_no_other()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        await WrongPasswordsAsync(factory, Email, Attacker, 5);

        // The attacker is shut out, even with the right password. The owner, from their own address, is not.
        Assert.Equal(HttpStatusCode.Unauthorized, await RightPasswordAsync(factory, Email, Attacker));
        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Owner));
        var lockout = Assert.Single(await LockoutsAsync(factory));
        Assert.Equal(Attacker, lockout.IpAddress);
        Assert.Equal(1, lockout.Level);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_anything()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        await WrongPasswordsAsync(factory, Email, Attacker, 4);

        Assert.Empty(await LockoutsAsync(factory));
        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Attacker));
    }

    [Fact]
    public async Task The_locked_out_address_can_still_try_another_account_which_has_a_count_of_its_own()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        await factory.CreateUserAsync("someone.else@example.test");

        await WrongPasswordsAsync(factory, Email, Attacker, 5);

        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, "someone.else@example.test", Attacker));
    }

    [Fact]
    public async Task Identity_s_own_account_wide_lockout_is_never_used()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        await WrongPasswordsAsync(factory, Email, Attacker, 5);

        var user = await factory.GetUserAsync(Email);
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);
    }

    // --- The ladder: 15 minutes, 1 hour, 4 hours ----------------------------------------------------------------------------

    [Fact]
    public async Task Each_lockout_of_the_same_address_within_24_hours_lasts_longer_up_to_four_hours()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var wait = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4), TimeSpan.FromHours(4) };

        foreach (var expected in wait)
        {
            await WrongPasswordsAsync(factory, Email, Attacker, 5);
            var latest = (await LockoutsAsync(factory)).Last();
            Assert.InRange((latest.LockedUntil - factory.Clock.GetUtcNow()).TotalSeconds, expected.TotalSeconds - 5, expected.TotalSeconds + 5);

            // Just before it ends the address is still shut out; just after, the right password works again.
            factory.Clock.Advance(expected - TimeSpan.FromSeconds(30));
            Assert.Equal(HttpStatusCode.Unauthorized, await RightPasswordAsync(factory, Email, Attacker));
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Attacker));
        }

        Assert.Equal([1, 2, 3, 4], (await LockoutsAsync(factory)).Select(l => l.Level));
    }

    [Fact]
    public async Task A_lockout_more_than_24_hours_ago_does_not_count_towards_the_next_step()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        await WrongPasswordsAsync(factory, Email, Attacker, 5);
        factory.Clock.Advance(TimeSpan.FromHours(25));

        await WrongPasswordsAsync(factory, Email, Attacker, 5);

        Assert.Equal([1, 1], (await LockoutsAsync(factory)).Select(l => l.Level));
    }

    [Fact]
    public async Task Attempts_while_locked_out_are_refused_and_do_not_pile_up_more_lockouts()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        await WrongPasswordsAsync(factory, Email, Attacker, 5);

        await WrongPasswordsAsync(factory, Email, Attacker, 20);

        Assert.Single(await LockoutsAsync(factory));
        Assert.Equal(5, await OpenFailuresAsync(factory, FailureKind.Password));
    }

    // --- The whole account: three places -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_whole_account_is_locked_only_when_wrong_passwords_come_from_three_addresses()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        // Many from two addresses: both are locked out, but the account as a whole is not.
        await WrongPasswordsAsync(factory, Email, "198.51.100.1", 5);
        await WrongPasswordsAsync(factory, Email, "198.51.100.2", 5);
        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Owner));

        // One from a third address: now it is an attack from many places, and the whole account is locked.
        await WrongPasswordsAsync(factory, Email, "198.51.100.3", 1);

        Assert.Equal(HttpStatusCode.Unauthorized, await RightPasswordAsync(factory, Email, Owner));
        var whole = Assert.Single(await LockoutsAsync(factory), l => l.IpAddress is null);
        Assert.Equal(1, whole.Level);
        Assert.InRange((whole.LockedUntil - factory.Clock.GetUtcNow()).TotalSeconds, 15 * 60 - 5, 15 * 60 + 5);
    }

    [Fact]
    public async Task The_whole_account_lockout_ends_and_one_more_wrong_password_does_not_bring_it_straight_back()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        foreach (var address in new[] { "198.51.100.1", "198.51.100.2", "198.51.100.3" })
        {
            await WrongPasswordsAsync(factory, Email, address, 1);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        await WrongPasswordsAsync(factory, Email, "198.51.100.1", 1);

        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Owner));
        Assert.Single(await LockoutsAsync(factory), l => l.IpAddress is null);
    }

    // --- Fifty failures against an admin change nothing about the admin ------------------------------------------------------------

    [Fact]
    public async Task Fifty_wrong_passwords_against_an_admin_never_change_their_status_or_their_standing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var target = admins[0];
        var session = factory.CreateApiClient();
        await session.SignInWithTwoFactorAsync(factory, target);

        for (var address = 1; address <= 10; address++)
        {
            await WrongPasswordsAsync(factory, target.Email!, $"198.51.100.{address}", 5);
        }

        var after = await factory.GetUserAsync(target.Email!);
        Assert.Equal(AccountStatus.Approved, after.AccountStatus);
        Assert.Equal([Role.Admin], await factory.RolesOfAsync(after));
        Assert.Equal(2, await factory.WithDbAsync(db => CaseRegister.Infrastructure.Approvals.AdminCensus.CountActiveAsync(db, default)));

        // The admin's own session carries on, and when the lockouts have run out they can sign in again.
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/probe/members-only")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, target.Email!, Owner));
        Assert.Equal(0, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "AccountSuspended")));
    }

    [Fact]
    public async Task Lockouts_never_suspend_however_many_there_are()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        for (var round = 0; round < 6; round++)
        {
            await WrongPasswordsAsync(factory, Email, Attacker, 5);
            factory.Clock.Advance(TimeSpan.FromHours(5));
        }

        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(Email)).AccountStatus);
        Assert.Equal(6, (await LockoutsAsync(factory)).Count);
    }

    // --- A full sign-in starts the counters again -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_full_sign_in_clears_the_counters_so_four_more_wrong_passwords_do_not_lock()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        await WrongPasswordsAsync(factory, Email, Owner, 4);
        Assert.Equal(4, await OpenFailuresAsync(factory, FailureKind.Password));

        // The right password alone does not forgive them: a stolen password must not clear the counter while codes are guessed.
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        Assert.Equal(4, await OpenFailuresAsync(factory, FailureKind.Password));
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner)).StatusCode);
        Assert.Equal(0, await OpenFailuresAsync(factory, FailureKind.Password));

        await WrongPasswordsAsync(factory, Email, Owner, 4);

        Assert.Empty(await LockoutsAsync(factory));
    }

    [Fact]
    public async Task A_full_sign_in_from_one_address_does_not_end_the_lockout_of_another_address()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        await WrongPasswordsAsync(factory, Email, Attacker, 5); // the attacker's address is locked out

        await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user, forwardedFor: Owner);

        Assert.Equal(HttpStatusCode.Unauthorized, await RightPasswordAsync(factory, Email, Attacker)); // still shut out
        var lockout = Assert.Single(await LockoutsAsync(factory));
        Assert.Null(lockout.ClearedAt);
    }

    // --- Wrong second-factor codes are counted apart ------------------------------------------------------------------------------

    [Fact]
    public async Task Wrong_codes_never_count_towards_the_password_lockout_and_wrong_passwords_never_count_towards_the_code_lock()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync("00000" + attempt, Owner)).StatusCode);
        }

        Assert.Equal(0, await OpenFailuresAsync(factory, FailureKind.Password));
        Assert.Equal(9, await OpenFailuresAsync(factory, FailureKind.SecondFactor));
        Assert.Empty(await LockoutsAsync(factory));

        await WrongPasswordsAsync(factory, Email, Attacker, 4);
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner)).StatusCode); // 9 is not 10
    }

    [Fact]
    public async Task Ten_wrong_codes_in_a_row_lock_the_authenticator_and_only_a_recovery_code_gets_in()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var session = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        var recoveryCodes = await TwoFactorFlows.EnrolFullyAsync(factory, session, user);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync("00000" + attempt, Owner)).StatusCode);
        }

        // A perfectly good code is now refused without being checked, and the answer says what to do.
        var locked = await client.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("recovery code", (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(), StringComparison.Ordinal);

        // A recovery code works, and it unlocks the authenticator again.
        var recovered = await client.VerifyTwoFactorAsync(recoveryCodes[0], Owner);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(0, await OpenFailuresAsync(factory, FailureKind.SecondFactor));
        var next = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        Assert.Equal(HttpStatusCode.OK, (await next.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner)).StatusCode);
    }

    [Fact]
    public async Task A_right_code_in_the_middle_breaks_the_run_so_it_has_to_be_ten_in_a_row()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            await client.VerifyTwoFactorAsync("00000" + attempt, Owner);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner)).StatusCode);
        var again = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        for (var attempt = 0; attempt < 9; attempt++)
        {
            await again.VerifyTwoFactorAsync("00000" + attempt, Owner);
        }

        Assert.Equal(HttpStatusCode.OK, (await again.VerifyTwoFactorAsync(factory.NextTotpCode(user), Owner)).StatusCode);
    }

    [Fact]
    public async Task Locking_the_authenticator_changes_no_status_and_locks_no_one_out_of_the_password_step()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user, Owner);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await client.VerifyTwoFactorAsync("00000" + attempt, Owner);
        }

        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(Email)).AccountStatus);
        Assert.Equal(HttpStatusCode.OK, await RightPasswordAsync(factory, Email, Owner));
        Assert.Empty(await LockoutsAsync(factory));
    }

    // --- Told, and written down -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_lockout_is_audited_and_tells_the_person_and_every_admin_in_words_that_name_nobody()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var bystander = await factory.CreateMemberAsync("bystander@example.test", agency, [Role.RegisteredAgent]);

        await WrongPasswordsAsync(factory, member.Email!, Attacker, 5);

        var entry = Assert.Single(await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "AccountLockedOut").ToListAsync()));
        Assert.Equal(member.Id, entry.ActorUserId);
        Assert.Equal(Attacker, entry.IpAddress);
        Assert.Contains("No status was changed", entry.Detail, StringComparison.Ordinal);

        var told = await factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.Type == NotificationType.AccountLockedOut).ToListAsync());
        Assert.Equal(new[] { admins[0].Id, admins[1].Id, member.Id }.Order(), told.Select(n => n.RecipientId).Order());
        Assert.DoesNotContain(told, n => n.RecipientId == bystander.Id);
        foreach (var note in told)
        {
            Assert.DoesNotContain("@", note.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("member", note.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_notification_says_where_the_attempts_came_from_when_the_proxy_said_so()
    {
        var text = CaseRegister.Application.Notifications.NotificationFactory.LockoutForUser(Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMinutes(15), new CaseRegister.Application.Auth.ClientLocation("RU", "Moscow"), wholeAccount: false).Text;

        Assert.Contains("from Moscow, RU", text, StringComparison.Ordinal);
        Assert.Contains("15 minutes", text, StringComparison.Ordinal);
    }

    // --- Changing a password counts too ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Guessing_the_current_password_from_a_session_counts_against_that_address_and_locks_it_like_any_other_guess()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, user, forwardedFor: Owner);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.ChangePasswordAsync("wrong password " + attempt, "another long passphrase", factory.NextTotpCode(user))).StatusCode);
        }

        Assert.Single(await LockoutsAsync(factory));
    }
}
