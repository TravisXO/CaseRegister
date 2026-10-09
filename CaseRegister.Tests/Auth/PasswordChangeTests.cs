using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class PasswordChangeTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";
    private const string NewPassword = "an entirely different passphrase";

    private async Task<(ApiFactory Factory, ApiClient Client)> SignedInAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email)).StatusCode);

        return (factory, client);
    }

    private static async Task<string> DetailAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!;

    [Fact]
    public async Task A_member_can_change_their_password_with_the_current_one()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var fresh = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await fresh.SignInAsync(Email, NewPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_password()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.ChangePasswordAsync("not my current password", NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("current password", await DetailAsync(response), StringComparison.OrdinalIgnoreCase);

        // Nothing changed.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().SignInAsync(Email, NewPassword)).StatusCode);
    }

    [Fact]
    public async Task A_session_alone_is_not_enough_to_take_over_an_account()
    {
        // The whole point of asking for the current password: an open laptop or a stolen cookie is not a stolen account.
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new { newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task Guessing_the_current_password_from_an_open_session_leads_to_lockout()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        for (var i = 0; i < 5; i++)
        {
            await client.ChangePasswordAsync("wrong guess " + i, NewPassword);
        }

        // Locked out: even the right current password is refused now, and so is signing in.
        var response = await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().SignInAsync(Email)).StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.LockoutEvents.CountAsync()));
    }

    [Fact]
    public async Task Changing_the_password_updates_the_security_stamp()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;
        var before = await factory.GetUserAsync(Email);

        await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        var after = await factory.GetUserAsync(Email);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
    }

    [Fact]
    public async Task The_session_that_changed_the_password_carries_on()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(6)); // past the stamp re-check
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Every_other_session_ends_when_the_password_changes()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;
        var otherDevice = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.SignInAsync(Email)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherDevice.GetAsync("/api/auth/session")).StatusCode);

        await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task The_new_password_must_meet_the_password_rules()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var tooShort = await client.ChangePasswordAsync(ApiFactory.Password, "short one");
        var personal = await client.ChangePasswordAsync(ApiFactory.Password, "member@example.test!!");
        factory.Breaches.BreachedPasswords.Add("a famous leaked password");
        var breached = await client.ChangePasswordAsync(ApiFactory.Password, "a famous leaked password");

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("at least 12", await DetailAsync(tooShort), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, personal.StatusCode);
        Assert.Contains("email", await DetailAsync(personal), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, breached.StatusCode);
        Assert.Contains("breach", await DetailAsync(breached), StringComparison.OrdinalIgnoreCase);

        // And the old password still works.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Fact]
    public async Task When_the_breach_check_is_down_the_change_fails_closed_and_asks_to_try_again()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;
        factory.Breaches.Unavailable = true;

        var response = await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("try again shortly", await DetailAsync(response), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }

    [Theory]
    [InlineData(129)]
    public async Task An_oversized_password_is_a_bad_request(int length)
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.ChangePasswordAsync(ApiFactory.Password, new string('x', length));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_field_is_a_bad_request()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new { currentPassword = "", newPassword = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Password_changes_and_failed_attempts_are_audited_without_the_passwords()
    {
        var (factory, client) = await SignedInAsync();
        await using var _ = factory;

        await client.ChangePasswordAsync("not my current password", NewPassword);
        await client.ChangePasswordAsync(ApiFactory.Password, NewPassword);

        var audit = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Id).ToListAsync());
        Assert.Contains(audit, record => record.Action == "PasswordChangeFailed");
        Assert.Contains(audit, record => record.Action == "PasswordChanged");

        var everything = string.Join('\n', audit.Select(record => $"{record.Subject}|{record.IpAddress}|{record.Detail}"));
        var logs = string.Join('\n', factory.Logs.Entries.Select(entry => entry.Message));
        foreach (var secret in new[] { ApiFactory.Password, NewPassword, "not my current password" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Only_a_signed_in_member_can_change_a_password()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        var response = await factory.CreateApiClient().ChangePasswordAsync(ApiFactory.Password, NewPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode);
    }
}
