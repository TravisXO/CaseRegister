using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class TwoFactorEnrolmentTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    // --- Until two-factor is enrolled, only enrolment is reachable ---------------------------

    [Fact]
    public async Task A_password_alone_for_an_account_without_two_factor_gives_a_restricted_session()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);

        var client = factory.CreateApiClient();
        var response = await client.SignInAsync(Email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(response));
        Assert.NotNull(ApiClient.SetCookieHeader(response, "__Host-cr"));

        var session = await (await client.GetAsync("/api/auth/session")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(session.GetProperty("twoFactorComplete").GetBoolean());
        Assert.Equal(user.Id.ToString(), session.GetProperty("userId").GetString());
    }

    [Theory]
    [InlineData("GET", "/probe/members-only")]
    [InlineData("GET", "/probe/unannotated")]
    [InlineData("GET", "/api/auth/sessions")]
    [InlineData("POST", "/api/auth/change-password")]
    [InlineData("POST", "/api/auth/2fa/recovery-codes")]
    public async Task Until_two_factor_is_enrolled_everything_else_is_forbidden(string method, string path)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        var response = await client.SendAsync(
            new HttpMethod(method),
            path,
            method == "POST" ? new { currentPassword = ApiFactory.Password, newPassword = "x", twoFactorCode = "123456", code = "123456" } : null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_restricted_session_can_reach_enrolment_the_session_check_and_sign_out()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/signed-in-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out")).StatusCode);
    }

    [Fact]
    public async Task Enrolment_needs_someone_signed_in()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email, twoFactor: false);
        var anonymous = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorFlows.ConfirmEnrolmentAsync(anonymous, "123456")).StatusCode);
    }

    // --- Enrolment: key, address, confirmation -----------------------------------------------

    [Fact]
    public async Task Starting_enrolment_returns_a_key_and_an_otpauth_address_for_the_qr_code()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        var (response, sharedKey, uri) = await TwoFactorFlows.StartEnrolmentAsync(client);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[A-Z2-7]{32}$", sharedKey);
        Assert.StartsWith("otpauth://totp/CaseRegister:member%40example.test?", uri, StringComparison.Ordinal);
        Assert.Contains($"secret={sharedKey}", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=CaseRegister", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_is_160_random_bits_and_is_different_every_time()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var first = await factory.CreateUserAsync("first@example.test", twoFactor: false);
        var second = await factory.CreateUserAsync("second@example.test", twoFactor: false);

        var (_, keyOne, _) = await TwoFactorFlows.StartEnrolmentAsync(await TwoFactorFlows.RestrictedSessionAsync(factory, first));
        var (_, keyTwo, _) = await TwoFactorFlows.StartEnrolmentAsync(await TwoFactorFlows.RestrictedSessionAsync(factory, second));

        Assert.Equal(20, Base32.Decode(keyOne!).Length);
        Assert.NotEqual(keyOne, keyTwo);
    }

    [Fact]
    public async Task Starting_enrolment_switches_nothing_on()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        await TwoFactorFlows.StartEnrolmentAsync(client);

        var record = await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync());
        Assert.Null(record.EnabledAt);
        Assert.Equal(0, await factory.WithDbAsync(db => db.RecoveryCodes.CountAsync()));
        Assert.False((await factory.GetUserAsync(Email)).TwoFactorEnabled);

        // Signing in again is still the restricted, enrolment-only session.
        var again = factory.CreateApiClient();
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(await again.SignInAsync(Email)));
    }

    [Fact]
    public async Task Starting_again_replaces_a_key_that_was_never_confirmed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        var (_, firstKey, _) = await TwoFactorFlows.StartEnrolmentAsync(client);
        var (_, secondKey, _) = await TwoFactorFlows.StartEnrolmentAsync(client);
        Assert.NotEqual(firstKey, secondKey);

        // The old key no longer works; the new one does.
        factory.RememberTwoFactorKey(user, Base32.Decode(firstKey!));
        Assert.Equal(HttpStatusCode.BadRequest, (await TwoFactorFlows.ConfirmEnrolmentAsync(client, factory.NextTotpCode(user))).StatusCode);

        factory.RememberTwoFactorKey(user, Base32.Decode(secondKey!));
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorFlows.ConfirmEnrolmentAsync(client, factory.NextTotpCode(user))).StatusCode);
    }

    [Fact]
    public async Task A_wrong_code_does_not_switch_two_factor_on()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        await TwoFactorFlows.StartEnrolmentAsync(client);

        var response = await TwoFactorFlows.ConfirmEnrolmentAsync(client, "000000");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync())).EnabledAt);
        Assert.Equal(0, await factory.WithDbAsync(db => db.RecoveryCodes.CountAsync()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task Confirming_without_starting_has_nothing_to_confirm()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        var response = await TwoFactorFlows.ConfirmEnrolmentAsync(client, "123456");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_code_switches_two_factor_on_and_returns_ten_recovery_codes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);

        var codes = await TwoFactorFlows.EnrolFullyAsync(factory, client, user);

        Assert.Equal(10, codes.Count);
        Assert.NotNull((await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync())).EnabledAt);
        Assert.True((await factory.GetUserAsync(Email)).TwoFactorEnabled);
        Assert.Equal(10, await factory.WithDbAsync(db => db.RecoveryCodes.CountAsync()));
    }

    [Fact]
    public async Task The_session_that_enrolled_is_upgraded_in_place_to_a_fully_signed_in_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/members-only")).StatusCode);

        await TwoFactorFlows.EnrolFullyAsync(factory, client, user);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
        var session = await (await client.GetAsync("/api/auth/session")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(session.GetProperty("twoFactorComplete").GetBoolean());

        // Replaced, not added to: still one session row.
        Assert.Equal(1, await factory.WithDbAsync(db => db.Sessions.CountAsync()));
    }

    [Fact]
    public async Task Enrolment_cannot_be_done_twice()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        await TwoFactorFlows.EnrolFullyAsync(factory, client, user);
        var keyBefore = (await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync())).EncryptedKey;

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await TwoFactorFlows.ConfirmEnrolmentAsync(client, factory.NextTotpCode(user))).StatusCode);

        // The key was not replaced.
        Assert.Equal(keyBefore, (await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync())).EncryptedKey);
    }

    [Fact]
    public async Task After_enrolment_the_next_sign_in_asks_for_a_code()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        await TwoFactorFlows.EnrolFullyAsync(factory, await TwoFactorFlows.RestrictedSessionAsync(factory, user), user);

        var next = factory.CreateApiClient();
        var response = await next.SignInAsync(Email);

        Assert.Equal("two-factor-required", await TwoFactorFlows.StatusAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await next.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
    }

    // --- Enrolment is audited, and the key is encrypted --------------------------------------

    [Fact]
    public async Task Enrolment_is_audited()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        await TwoFactorFlows.StartEnrolmentAsync(client);
        await TwoFactorFlows.ConfirmEnrolmentAsync(client, "000000");
        await TwoFactorFlows.EnrolFullyAsync(factory, client, user);

        var audit = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Id).ToListAsync());

        var enrolled = Assert.Single(audit, record => record.Action == "TwoFactorEnrolled");
        Assert.Equal(user.Id, enrolled.ActorUserId);
        Assert.Contains(audit, record => record.Action == "TwoFactorFailed");
    }

    [Fact]
    public async Task The_authenticator_key_is_encrypted_at_rest()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        var (_, sharedKey, _) = await TwoFactorFlows.StartEnrolmentAsync(client);
        var keyBytes = Base32.Decode(sharedKey!);

        var stored = await factory.WithDbAsync(db => db.Database.SqlQuery<string>($"SELECT encrypted_key AS \"Value\" FROM user_authenticators").SingleAsync());

        // Not the base32 key, not the base64 of the key, and a data protection payload.
        Assert.DoesNotContain(sharedKey!, stored, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(keyBytes), stored, StringComparison.Ordinal);
        Assert.StartsWith("CfDJ8", stored, StringComparison.Ordinal);

        // And it really decrypts back to the key, with the right purpose, and only with it.
        using var scope = factory.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<AuthenticatorKeyProtector>();
        Assert.Equal(keyBytes, protector.Unprotect(stored));

        var wrongPurpose = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("some.other.purpose");
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => wrongPurpose.Unprotect(stored));
    }

    [Fact]
    public async Task Keys_and_codes_never_reach_the_audit_table_or_the_logs()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        var (_, sharedKey, _) = await TwoFactorFlows.StartEnrolmentAsync(client);
        factory.RememberTwoFactorKey(user, Base32.Decode(sharedKey!));
        var code = factory.NextTotpCode(user);
        var confirm = await TwoFactorFlows.ConfirmEnrolmentAsync(client, code);
        var recoveryCodes = await TwoFactorFlows.RecoveryCodesAsync(confirm);
        var wrongCode = "654321";
        await factory.CreateApiClient().SignInAsync(Email);
        await (await TwoFactorFlows.AtTheCodeStepAsync(factory, user)).VerifyTwoFactorAsync(wrongCode);

        var audit = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        var everything = string.Join('\n', audit.Select(record => $"{record.Subject}|{record.IpAddress}|{record.Detail}"))
            + '\n' + string.Join('\n', factory.Logs.Entries.Select(entry => entry.Message));

        foreach (var secret in recoveryCodes.Concat([sharedKey!, code, wrongCode]))
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Restricted sessions count, and are the same sessions ---------------------------------

    [Fact]
    public async Task Another_users_enrolment_is_not_touched()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var alice = await factory.CreateUserAsync("alice@example.test", twoFactor: false);
        var bob = await factory.CreateUserAsync("bob@example.test", twoFactor: false);

        await TwoFactorFlows.EnrolFullyAsync(factory, await TwoFactorFlows.RestrictedSessionAsync(factory, alice), alice);

        var bobsRecords = await factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == bob.Id));
        Assert.Equal(0, bobsRecords);
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(await factory.CreateApiClient().SignInAsync("bob@example.test")));
    }
}
