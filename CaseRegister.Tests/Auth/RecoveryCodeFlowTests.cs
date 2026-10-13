using System.Net;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class RecoveryCodeFlowTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private sealed record Enrolled(ApiFactory Factory, ApplicationUser User, List<string> Codes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    /// <summary>An account that enrolled through the real API, so it has genuine recovery codes.</summary>
    private async Task<Enrolled> EnrolledAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var codes = await TwoFactorFlows.EnrolFullyAsync(factory, await TwoFactorFlows.RestrictedSessionAsync(factory, user), user);

        return new Enrolled(factory, user, codes);
    }

    // --- Shown once, stored hashed ---------------------------------------------------------------

    [Fact]
    public async Task Enrolling_returns_ten_recovery_codes_in_a_readable_form()
    {
        await using var enrolled = await EnrolledAsync();

        Assert.Equal(10, enrolled.Codes.Count);
        Assert.All(enrolled.Codes, code => Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$", code));
        Assert.Equal(10, enrolled.Codes.Distinct().Count());
    }

    [Fact]
    public async Task Only_hashes_are_stored_never_the_codes()
    {
        await using var enrolled = await EnrolledAsync();

        var stored = await enrolled.Factory.WithDbAsync(db => db.RecoveryCodes.AsNoTracking().ToListAsync());

        Assert.Equal(10, stored.Count);
        Assert.All(stored, record => Assert.Matches("^[0-9A-F]{64}$", record.CodeHash));
        Assert.Empty(stored.Select(record => record.CodeHash).Intersect(enrolled.Codes.Select(code => code.Replace("-", string.Empty, StringComparison.Ordinal))));

        // A search of the whole table for any code, in either written form, finds nothing.
        var everything = string.Join('\n', stored.Select(record => record.CodeHash));
        foreach (var code in enrolled.Codes)
        {
            Assert.DoesNotContain(code, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal), everything, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_codes_are_shown_once_and_no_endpoint_returns_them_again()
    {
        await using var enrolled = await EnrolledAsync();
        var client = enrolled.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(enrolled.Factory, enrolled.User);

        // Everything a signed-in member can read back about themselves.
        var responses = new[]
        {
            await client.GetAsync("/api/auth/session"),
            await client.GetAsync("/api/auth/sessions"),
            await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol"),
        };

        foreach (var response in responses)
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.All(enrolled.Codes, code => Assert.DoesNotContain(code, body, StringComparison.OrdinalIgnoreCase));
        }
    }

    // --- Using one ------------------------------------------------------------------------------

    [Fact]
    public async Task A_recovery_code_signs_in_instead_of_an_authenticator_code()
    {
        await using var enrolled = await EnrolledAsync();
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);

        var response = await client.VerifyTwoFactorAsync(enrolled.Codes[0]);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("nohyphens")]
    [InlineData("spaces")]
    public async Task However_it_is_typed_a_recovery_code_works(string style)
    {
        await using var enrolled = await EnrolledAsync();
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
        var typed = style switch
        {
            "lower" => enrolled.Codes[0].ToLowerInvariant(),
            "nohyphens" => enrolled.Codes[0].Replace("-", string.Empty, StringComparison.Ordinal),
            _ => enrolled.Codes[0].Replace('-', ' '),
        };

        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(typed)).StatusCode);
    }

    [Fact]
    public async Task Each_recovery_code_works_once_and_only_once()
    {
        await using var enrolled = await EnrolledAsync();
        var first = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
        Assert.Equal(HttpStatusCode.OK, (await first.VerifyTwoFactorAsync(enrolled.Codes[0])).StatusCode);
        await first.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        var again = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);

        Assert.Equal(HttpStatusCode.Unauthorized, (await again.VerifyTwoFactorAsync(enrolled.Codes[0])).StatusCode);

        // The others are untouched.
        Assert.Equal(HttpStatusCode.OK, (await again.VerifyTwoFactorAsync(enrolled.Codes[1])).StatusCode);
    }

    [Fact]
    public async Task A_used_code_is_marked_used_and_the_rest_are_not()
    {
        await using var enrolled = await EnrolledAsync();
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);

        await client.VerifyTwoFactorAsync(enrolled.Codes[3]);

        var stored = await enrolled.Factory.WithDbAsync(db => db.RecoveryCodes.AsNoTracking().ToListAsync());
        Assert.Single(stored, record => record.UsedAt is not null);
        Assert.Equal(9, stored.Count(record => record.UsedAt is null));
    }

    [Fact]
    public async Task One_recovery_code_used_twice_at_the_same_instant_works_only_once()
    {
        await using var enrolled = await EnrolledAsync();

        // Several rounds, each with a different code, and the exact outcomes every time.
        for (var round = 0; round < 5; round++)
        {
            var one = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
            var two = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);

            var results = await Task.WhenAll(one.VerifyTwoFactorAsync(enrolled.Codes[round]), two.VerifyTwoFactorAsync(enrolled.Codes[round]));

            var statuses = results.Select(result => result.StatusCode).Order().ToList();
            Assert.True(statuses.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.Unauthorized]), $"round {round}: {string.Join(", ", statuses)}");
        }
    }

    [Fact]
    public async Task One_users_recovery_code_does_not_work_for_another_user()
    {
        await using var enrolled = await EnrolledAsync();
        var other = await enrolled.Factory.CreateUserAsync("other@example.test");
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, other);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync(enrolled.Codes[0])).StatusCode);
    }

    [Fact]
    public async Task Five_wrong_recovery_codes_lock_the_account_like_five_wrong_authenticator_codes()
    {
        await using var enrolled = await EnrolledAsync();
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);

        for (var i = 0; i < 5; i++)
        {
            await client.VerifyTwoFactorAsync($"AAAA-BBBB-CCCC-DD{i + 2}{i + 3}");
        }

        Assert.Equal(1, await enrolled.Factory.WithDbAsync(db => db.LockoutEvents.CountAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync(enrolled.Codes[0])).StatusCode);
    }

    [Fact]
    public async Task Using_a_recovery_code_is_audited()
    {
        await using var enrolled = await EnrolledAsync();
        var client = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
        await client.VerifyTwoFactorAsync(enrolled.Codes[0]);

        var audit = await enrolled.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Id).ToListAsync());

        var used = Assert.Single(audit, record => record.Action == "RecoveryCodeUsed");
        Assert.Equal(enrolled.User.Id, used.ActorUserId);
        Assert.Contains(audit, record => record.Action == "SignInSucceeded" && record.Detail!.Contains("recovery code", StringComparison.Ordinal));
        Assert.DoesNotContain(enrolled.Codes[0], used.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // --- Regenerating ---------------------------------------------------------------------------

    [Fact]
    public async Task Regenerating_gives_ten_new_codes_and_retires_every_old_one()
    {
        await using var enrolled = await EnrolledAsync();
        var client = enrolled.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(enrolled.Factory, enrolled.User);

        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = enrolled.Factory.NextTotpCode(enrolled.User) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = await TwoFactorFlows.RecoveryCodesAsync(response);
        Assert.Equal(10, fresh.Count);
        Assert.Empty(fresh.Intersect(enrolled.Codes));

        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");
        var oldCode = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldCode.VerifyTwoFactorAsync(enrolled.Codes[0])).StatusCode);
        var newCode = await TwoFactorFlows.AtTheCodeStepAsync(enrolled.Factory, enrolled.User);
        Assert.Equal(HttpStatusCode.OK, (await newCode.VerifyTwoFactorAsync(fresh[0])).StatusCode);
    }

    [Fact]
    public async Task Regenerating_needs_a_valid_authenticator_code()
    {
        await using var enrolled = await EnrolledAsync();
        var client = enrolled.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(enrolled.Factory, enrolled.User);

        var wrong = await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = "000000" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(10, await enrolled.Factory.WithDbAsync(db => db.RecoveryCodes.CountAsync(record => record.RevokedAt == null)));
    }

    [Fact]
    public async Task A_recovery_code_cannot_be_used_to_make_more_recovery_codes()
    {
        await using var enrolled = await EnrolledAsync();
        var client = enrolled.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(enrolled.Factory, enrolled.User);

        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = enrolled.Codes[0] });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Regenerating_is_audited_and_marks_the_old_codes_revoked()
    {
        await using var enrolled = await EnrolledAsync();
        var client = enrolled.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(enrolled.Factory, enrolled.User);
        await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = enrolled.Factory.NextTotpCode(enrolled.User) });

        var audit = await enrolled.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        var stored = await enrolled.Factory.WithDbAsync(db => db.RecoveryCodes.AsNoTracking().ToListAsync());

        Assert.Single(audit, record => record.Action == "RecoveryCodesRegenerated");
        Assert.Equal(20, stored.Count);
        Assert.Equal(10, stored.Count(record => record.RevokedAt is not null));
        Assert.Equal(10, stored.Count(record => record.RevokedAt is null && record.UsedAt is null));
    }

    [Fact]
    public async Task Regenerating_needs_a_fully_signed_in_session()
    {
        await using var enrolled = await EnrolledAsync();

        var anonymous = await enrolled.Factory.CreateApiClient().SendAsync(HttpMethod.Post, "/api/auth/2fa/recovery-codes", new { code = "123456" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
