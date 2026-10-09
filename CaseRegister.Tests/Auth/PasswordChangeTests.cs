using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class PasswordChangeTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";
    private const string NewPassword = "an entirely different passphrase";

    private sealed record Member(ApiFactory Factory, ApiClient Client, ApplicationUser User) : IAsyncDisposable
    {
        public string Code() => Factory.NextTotpCode(User);

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private async Task<Member> SignedInAsync(int twoFactorAttemptsPerMinute = 10_000)
    {
        var factory = await ApiFactory.CreateAsync(postgres, twoFactorAttemptsPerMinute: twoFactorAttemptsPerMinute);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);

        return new Member(factory, client, user);
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    [Fact]
    public async Task A_member_can_change_their_password_with_the_current_password_and_a_code()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var fresh = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await fresh.SignInWithTwoFactorAsync(member.Factory, member.User, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_password()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.ChangePasswordAsync("not my current password", NewPassword, member.Code());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("current password", await DetailAsync(response), StringComparison.OrdinalIgnoreCase);

        // Nothing changed.
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Factory.CreateApiClient().SignInAsync(Email, NewPassword)).StatusCode);
    }

    // --- The second factor -------------------------------------------------------------------

    [Fact]
    public async Task Changing_the_password_also_needs_a_valid_two_factor_code()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, "000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("two-factor", await DetailAsync(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Factory.CreateApiClient().SignInAsync(Email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task The_right_password_and_current_session_are_not_enough_without_a_code()
    {
        await using var member = await SignedInAsync();

        var withoutCode = await member.Client.SendAsync(
            HttpMethod.Post,
            "/api/auth/change-password",
            new { currentPassword = ApiFactory.Password, newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, withoutCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task A_code_that_was_already_used_cannot_be_used_again_to_change_the_password()
    {
        await using var member = await SignedInAsync();
        var used = member.Factory.CodeForStep(member.User, member.Factory.CurrentStep); // the step signing in just used

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, used);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_counts_towards_lockout()
    {
        await using var member = await SignedInAsync();

        for (var i = 0; i < 5; i++)
        {
            await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, "000000");
        }

        Assert.Equal(1, await member.Factory.WithDbAsync(db => db.LockoutEvents.CountAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Too_many_codes_in_a_minute_get_429_and_the_password_is_not_changed()
    {
        await using var member = await SignedInAsync(twoFactorAttemptsPerMinute: 3);

        // Signing in used one attempt; two more wrong ones use the rest.
        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, "000000");
        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, "000000");

        var limited = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task A_recovery_code_also_counts_as_a_second_factor_and_is_used_up()
    {
        await using var member = await SignedInAsync();
        var codes = await RegenerateAsync(member);

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, codes[0]);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var again = await member.Client.ChangePasswordAsync(NewPassword, ApiFactory.Password, codes[0]);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    private static async Task<List<string>> RegenerateAsync(Member member)
    {
        var response = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = member.Code() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return [.. body.GetProperty("recoveryCodes").EnumerateArray().Select(code => code.GetString()!)];
    }

    // --- Security stamp and sessions ---------------------------------------------------------

    [Fact]
    public async Task Changing_the_password_updates_the_security_stamp()
    {
        await using var member = await SignedInAsync();
        var before = await member.Factory.GetUserAsync(Email);

        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        var after = await member.Factory.GetUserAsync(Email);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task The_session_that_changed_the_password_carries_on_fully_signed_in()
    {
        await using var member = await SignedInAsync();

        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/probe/members-only")).StatusCode);
        member.Factory.Clock.Advance(TimeSpan.FromMinutes(6)); // past the stamp re-check
        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task Every_other_session_ends_when_the_password_changes()
    {
        await using var member = await SignedInAsync();
        var otherDevice = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.SignInWithTwoFactorAsync(member.Factory, member.User)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/probe/members-only")).StatusCode);

        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        member.Factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/probe/members-only")).StatusCode);
    }

    // --- The new password --------------------------------------------------------------------

    [Fact]
    public async Task The_new_password_must_meet_the_password_rules()
    {
        await using var member = await SignedInAsync();

        var tooShort = await member.Client.ChangePasswordAsync(ApiFactory.Password, "short one", member.Code());
        var personal = await member.Client.ChangePasswordAsync(ApiFactory.Password, "member@example.test!!", member.Code());
        member.Factory.Breaches.BreachedPasswords.Add("a famous leaked password");
        var breached = await member.Client.ChangePasswordAsync(ApiFactory.Password, "a famous leaked password", member.Code());

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("at least 12", await DetailAsync(tooShort), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, personal.StatusCode);
        Assert.Contains("email", await DetailAsync(personal), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, breached.StatusCode);
        Assert.Contains("breach", await DetailAsync(breached), StringComparison.OrdinalIgnoreCase);

        // And the old password still works.
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task When_the_breach_check_is_down_the_change_fails_closed_and_asks_to_try_again()
    {
        await using var member = await SignedInAsync();
        member.Factory.Breaches.Unavailable = true;

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("try again shortly", await DetailAsync(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task An_oversized_password_is_a_bad_request()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.ChangePasswordAsync(ApiFactory.Password, new string('x', 129), member.Code());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_field_is_a_bad_request()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.SendAsync(
            HttpMethod.Post,
            "/api/auth/change-password",
            new { currentPassword = "", newPassword = "", twoFactorCode = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Audit -------------------------------------------------------------------------------

    [Fact]
    public async Task Password_changes_and_failed_attempts_are_audited_without_any_secret()
    {
        await using var member = await SignedInAsync();

        await member.Client.ChangePasswordAsync("not my current password", NewPassword, member.Code());
        await member.Client.ChangePasswordAsync(ApiFactory.Password, NewPassword, member.Code());

        var audit = await member.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Id).ToListAsync());
        Assert.Contains(audit, record => record.Action == "PasswordChangeFailed");
        Assert.Contains(audit, record => record.Action == "PasswordChanged");

        var everything = string.Join('\n', audit.Select(record => $"{record.Subject}|{record.IpAddress}|{record.Detail}"));
        var logs = string.Join('\n', member.Factory.Logs.Entries.Select(entry => entry.Message));
        foreach (var secret in new[] { ApiFactory.Password, NewPassword, "not my current password" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }

    // --- Who may ---------------------------------------------------------------------------

    [Fact]
    public async Task Only_a_fully_signed_in_member_can_change_a_password()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        var anonymous = await factory.CreateApiClient().ChangePasswordAsync(ApiFactory.Password, NewPassword, "123456");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        // Password step done but no code yet: still not enough.
        var halfWay = factory.CreateApiClient();
        await halfWay.SignInAsync(Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await halfWay.ChangePasswordAsync(ApiFactory.Password, NewPassword, factory.NextTotpCode(user))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }
}
