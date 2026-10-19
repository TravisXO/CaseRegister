using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class SignInTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    // --- Who can sign in ---------------------------------------------------------------------

    [Fact]
    public async Task An_approved_member_signs_in_with_a_password_and_a_code()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        var response = await client.SignInWithTwoFactorAsync(factory, user);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
        var session = await (await client.GetAsync("/api/auth/session")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(session.GetProperty("twoFactorComplete").GetBoolean());
    }

    [Fact]
    public async Task The_right_password_alone_opens_no_session_when_the_account_has_two_factor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        var response = await client.SignInAsync(Email);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("two-factor-required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Null(ApiClient.SetCookieHeader(response, "__Host-cr"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task The_email_can_be_typed_in_any_case_with_stray_spaces()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        var response = await client.SignInAsync("  MEMBER@Example.TEST ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(AccountStatus.Approved, HttpStatusCode.OK)]
    [InlineData(AccountStatus.Pending, HttpStatusCode.Unauthorized)]
    [InlineData(AccountStatus.Rejected, HttpStatusCode.Unauthorized)]
    [InlineData(AccountStatus.Suspended, HttpStatusCode.Unauthorized)]
    public async Task Only_an_approved_account_can_sign_in(AccountStatus status, HttpStatusCode expected)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, status);
        var client = factory.CreateApiClient();

        var response = await client.SignInAsync(Email);

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
        }
        else
        {
            // Correct password, but no way in: no step cookie to carry on with, no session.
            Assert.Null(ApiClient.SetCookieHeader(response, "__Host-cr-2fa"));
            Assert.Null(ApiClient.SetCookieHeader(response, "__Host-cr"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        }
    }

    [Fact]
    public async Task An_account_approved_later_can_then_sign_in()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email, AccountStatus.Pending);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(Email)).StatusCode);

        await SetStatusAsync(factory, Email, AccountStatus.Approved);

        Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email)).StatusCode);
    }

    // --- No account enumeration --------------------------------------------------------------

    private static async Task<(string Scenario, HttpStatusCode Status, string Title, string Detail, string Type)> FailureShapeAsync(
        ApiFactory factory,
        string scenario)
    {
        var client = factory.CreateApiClient();

        var response = scenario switch
        {
            "unknown email" => await client.SignInAsync("nobody@example.test"),
            "wrong password" => await client.SignInAsync(Email, "not the right password"),
            "not approved" => await client.SignInAsync("pending@example.test"),
            "suspended" => await client.SignInAsync("suspended@example.test"),
            "locked out" => await client.SignInAsync("locked@example.test"),
            "bad bot token" => await client.SignInAsync(Email, turnstileToken: "forged"),
            "no bot token" => await client.SignInAsync(Email, turnstileToken: null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // traceId and the like vary per request; the parts a person reads must not.
        return (
            scenario,
            response.StatusCode,
            body.GetProperty("title").GetString()!,
            body.GetProperty("detail").GetString()!,
            body.GetProperty("type").GetString()!);
    }

    private static async Task SeedFailureCasesAsync(ApiFactory factory)
    {
        await factory.CreateUserAsync(Email);
        await factory.CreateUserAsync("pending@example.test", AccountStatus.Pending);
        await factory.CreateUserAsync("suspended@example.test", AccountStatus.Suspended);
        await factory.CreateUserAsync("locked@example.test");
        await LockOutAsync(factory, "locked@example.test");
    }

    [Fact]
    public async Task Every_kind_of_failed_sign_in_gets_exactly_the_same_response()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await SeedFailureCasesAsync(factory);

        var shapes = new List<(string Scenario, HttpStatusCode Status, string Title, string Detail, string Type)>();
        foreach (var scenario in new[] { "unknown email", "wrong password", "not approved", "suspended", "locked out", "bad bot token", "no bot token" })
        {
            shapes.Add(await FailureShapeAsync(factory, scenario));
        }

        var first = shapes[0];
        Assert.Equal(HttpStatusCode.Unauthorized, first.Status);
        Assert.All(shapes, shape =>
        {
            Assert.Equal(first.Status, shape.Status);
            Assert.Equal(first.Title, shape.Title);
            Assert.Equal(first.Detail, shape.Detail);
            Assert.Equal(first.Type, shape.Type);
        });

        // And the message itself says nothing about why.
        Assert.DoesNotContain("password", first.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exist", first.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lock", first.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approv", first.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown email")]
    [InlineData("wrong password")]
    [InlineData("not approved")]
    [InlineData("suspended")]
    [InlineData("locked out")]
    [InlineData("bad bot token")]
    [InlineData("no bot token")]
    public async Task Every_failed_sign_in_does_exactly_one_password_hash_so_the_response_time_is_the_same(string scenario)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await SeedFailureCasesAsync(factory);
        var before = factory.PasswordVerifications.Count;

        await FailureShapeAsync(factory, scenario);

        // A real account gets its real check; every other case gets a dummy check of the same cost.
        Assert.Equal(1, factory.PasswordVerifications.Count - before);
    }

    [Fact]
    public async Task A_successful_sign_in_does_one_hash_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var before = factory.PasswordVerifications.Count;

        await factory.CreateApiClient().SignInAsync(Email);

        Assert.Equal(1, factory.PasswordVerifications.Count - before);
    }

    // --- Request validation ------------------------------------------------------------------

    [Fact]
    public async Task An_oversized_password_is_refused_before_it_reaches_the_hash()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var before = factory.PasswordVerifications.Count;

        var response = await factory.CreateApiClient().SignInAsync(Email, new string('x', 129));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, factory.PasswordVerifications.Count);
        Assert.Empty(factory.Turnstile.Calls);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("member@example.test", "")]
    public async Task A_sign_in_without_an_email_or_password_is_a_bad_request(string email, string password)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().SignInAsync(email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Turnstile ---------------------------------------------------------------------------

    [Fact]
    public async Task Turnstile_is_checked_on_sign_in_with_the_token_the_page_sent()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        await factory.CreateApiClient().SignInAsync(Email);

        var call = Assert.Single(factory.Turnstile.Calls);
        Assert.Equal(FakeTurnstileVerifier.ValidToken, call.Token);
    }

    [Fact]
    public async Task A_failed_bot_check_refuses_the_sign_in_even_with_the_right_password()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        var response = await client.SignInAsync(Email, turnstileToken: "not-valid");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Fact]
    public async Task A_failed_bot_check_does_not_count_as_a_wrong_password()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        for (var i = 0; i < 8; i++)
        {
            await client.SignInAsync(Email, turnstileToken: "not-valid");
        }

        // A bot cannot lock a real member out by sending junk without solving the challenge.
        Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email)).StatusCode);
    }

    // --- Lockout (the full set of rules is in SignInThrottleTests) -----------------------------

    [Fact]
    public async Task Five_wrong_passwords_lock_the_address_they_came_from_even_against_the_right_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(Email, "wrong password " + i)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(Email)).StatusCode);
        Assert.Single(await factory.WithDbAsync(db => db.SignInLockouts.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task The_first_lockout_lasts_15_minutes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        for (var i = 0; i < 5; i++)
        {
            await client.SignInAsync(Email, "wrong password " + i);
        }

        var lockout = await factory.WithDbAsync(db => db.SignInLockouts.AsNoTracking().SingleAsync());
        Assert.InRange((lockout.LockedUntil - factory.Clock.GetUtcNow()).TotalSeconds, 15 * 60 - 5, 15 * 60 + 5);
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_and_only_finishing_two_factor_clears_the_count()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        for (var i = 0; i < 4; i++)
        {
            await client.SignInAsync(Email, "wrong password " + i);
        }

        // The right password gets through the first step but does not forgive the earlier failures:
        // someone with a stolen password must not be able to clear the counter while guessing codes.
        Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email)).StatusCode);
        Assert.Equal(4, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));

        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));
    }

    [Fact]
    public async Task Once_the_lockout_has_passed_the_right_password_works_again()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        await LockOutAsync(factory, Email);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(Email)).StatusCode);

        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email)).StatusCode);
    }

    [Fact]
    public async Task Attempts_while_locked_out_do_not_pile_up_more_lockouts()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        await LockOutAsync(factory, Email);

        for (var i = 0; i < 10; i++)
        {
            await client.SignInAsync(Email, "wrong password " + i);
        }

        Assert.Equal(1, await factory.WithDbAsync(db => db.SignInLockouts.CountAsync()));
        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(Email)).AccountStatus);
    }

    // --- Lockout never changes anyone's status ---------------------------------------------------

    [Fact]
    public async Task However_many_lockouts_there_are_the_account_is_never_suspended_and_its_sessions_carry_on()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var session = factory.CreateApiClient();
        await session.SignInWithTwoFactorAsync(factory, user);
        var stampBefore = (await factory.GetUserAsync(Email)).SecurityStamp;

        for (var round = 0; round < 3; round++)
        {
            await LockOutAsync(factory, Email);
            if (round == 0)
            {
                Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/auth/session")).StatusCode); // the open session carries on
            }
            factory.Clock.Advance(TimeSpan.FromHours(5));
        }

        var after = await factory.GetUserAsync(Email);
        Assert.Equal(AccountStatus.Approved, after.AccountStatus);
        Assert.Equal(stampBefore, after.SecurityStamp);
        var again = await factory.CreateApiClient().SignInAsync(Email);
        Assert.True(again.StatusCode == HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
    }

    // --- Audit -------------------------------------------------------------------------------

    private static Task<List<Infrastructure.Persistence.AuditLogRecord>> AuditAsync(ApiFactory factory) =>
        factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(record => record.Id).ToListAsync());

    [Fact]
    public async Task A_successful_sign_in_is_audited_with_who_and_when()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user);

        var entry = Assert.Single(await AuditAsync(factory), record => record.Action == "SignInSucceeded");
        Assert.Equal(user.Id, entry.ActorUserId);
        // Postgres keeps microseconds; .NET keeps 100-nanosecond ticks.
        Assert.True((factory.Clock.GetUtcNow() - entry.CreatedAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Failed_sign_ins_are_audited_whether_or_not_the_account_exists()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        await client.SignInAsync(Email, "wrong password");
        await client.SignInAsync("stranger@example.test");

        var failures = (await AuditAsync(factory)).Where(record => record.Action == "SignInFailed").ToList();
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, record => record.ActorUserId == user.Id);
        Assert.Contains(failures, record => record.ActorUserId is null);
    }

    [Fact]
    public async Task Lockouts_are_audited_and_no_suspension_is_ever_recorded()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        for (var round = 0; round < 3; round++)
        {
            await LockOutAsync(factory, Email);
            factory.Clock.Advance(TimeSpan.FromHours(5));
        }

        var actions = (await AuditAsync(factory)).Select(record => record.Action).ToList();
        Assert.Equal(3, actions.Count(action => action == "AccountLockedOut"));
        Assert.DoesNotContain("AccountSuspended", actions);
        Assert.All(
            (await AuditAsync(factory)).Where(record => record.Action is "AccountLockedOut"),
            record => Assert.Equal(user.Id, record.ActorUserId));
    }
    [Fact]
    public async Task No_password_ever_reaches_the_audit_table_or_the_logs()
    {
        const string correct = "Sup3r-secret passphrase one";
        const string wrong = "Wr0ng-secret passphrase two";
        const string typedIntoTheEmailBox = "Typed-Into-Email-Box-Passphrase";
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email, password: correct);
        var client = factory.CreateApiClient();

        await client.SignInAsync(Email, wrong);
        await client.SignInAsync(Email, correct);
        await client.SignInAsync(typedIntoTheEmailBox, wrong);
        for (var i = 0; i < 5; i++)
        {
            await client.SignInAsync(Email, wrong + i);
        }

        var everything = string.Join(
            '\n',
            (await AuditAsync(factory)).Select(record =>
                $"{record.Action}|{record.ActorUserId}|{record.Subject}|{record.IpAddress}|{record.Detail}"));
        var logs = string.Join('\n', factory.Logs.Entries.Select(entry => entry.Message));

        foreach (var secret in new[] { correct, wrong, typedIntoTheEmailBox, "passphrase" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(secret, logs, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_audit_log_never_stores_the_raw_email_a_sign_in_was_attempted_with()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.CreateApiClient().SignInAsync("stranger@example.test");

        var entry = Assert.Single(await AuditAsync(factory));
        Assert.DoesNotContain("stranger", entry.Subject ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("email-hmac:p1:", entry.Subject, StringComparison.Ordinal);
    }

    // --- Helpers -----------------------------------------------------------------------------

    private static async Task LockOutAsync(ApiFactory factory, string email)
    {
        var client = factory.CreateApiClient();
        for (var i = 0; i < 5; i++)
        {
            await client.SignInAsync(email, "wrong password " + i);
        }
    }

    private static async Task SetStatusAsync(ApiFactory factory, string email, AccountStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByEmailAsync(email))!;
        user.AccountStatus = status;

        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }
}
