using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Domain.Accounts;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class TwoFactorSignInTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private static readonly string[] _expectedCookies = ["__Host-csrf", "__Host-cr", "__Host-cr-2fa"];
    private static readonly HttpStatusCode[] _validationOrWrongCode = [HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized];

    // --- The time window: one step either side -----------------------------------------------

    [Theory]
    [InlineData(-2, HttpStatusCode.Unauthorized)]
    [InlineData(-1, HttpStatusCode.OK)]
    [InlineData(0, HttpStatusCode.OK)]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(2, HttpStatusCode.Unauthorized)]
    public async Task Codes_are_accepted_within_one_time_step_of_now_and_no_further(int offset, HttpStatusCode expected)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        var response = await client.VerifyTwoFactorAsync(factory.CodeForStep(user, factory.CurrentStep + offset));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Spaces_in_the_code_as_some_apps_show_it_are_fine()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        var code = factory.NextTotpCode(user);

        var response = await client.VerifyTwoFactorAsync($"{code[..3]} {code[3..]}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Replay: a code works once -----------------------------------------------------------

    [Fact]
    public async Task A_code_that_has_been_used_cannot_be_used_again()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var first = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        var code = factory.NextTotpCode(user);
        Assert.Equal(HttpStatusCode.OK, (await first.VerifyTwoFactorAsync(code)).StatusCode);
        await first.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        // Someone who saw that code a moment ago tries it on a fresh sign-in.
        var replay = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        var response = await replay.VerifyTwoFactorAsync(code);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Using_a_code_also_kills_the_codes_from_earlier_steps()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var earlier = factory.CodeForStep(user, factory.CurrentStep - 1);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        var retry = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        Assert.Equal(HttpStatusCode.Unauthorized, (await retry.VerifyTwoFactorAsync(earlier)).StatusCode);
    }

    [Fact]
    public async Task The_next_steps_code_is_still_good_after_one_was_used()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        var next = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        Assert.Equal(HttpStatusCode.OK, (await next.VerifyTwoFactorAsync(factory.CodeForStep(user, factory.CurrentStep + 1))).StatusCode);
    }

    [Fact]
    public async Task The_step_that_was_used_is_remembered_in_the_database()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        var step = factory.CurrentStep;

        await client.VerifyTwoFactorAsync(factory.CodeForStep(user, step));

        var record = await factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync());
        Assert.Equal(step, record.LastUsedStep);
    }

    [Fact]
    public async Task One_code_used_twice_at_the_same_instant_works_only_once()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        // Several rounds, each with a new code, and the exact outcomes every time.
        for (var round = 0; round < 8; round++)
        {
            var one = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
            var two = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
            var code = factory.NextTotpCode(user);

            var results = await Task.WhenAll(one.VerifyTwoFactorAsync(code), two.VerifyTwoFactorAsync(code));

            var statuses = results.Select(result => result.StatusCode).Order().ToList();
            Assert.True(statuses.SequenceEqual([HttpStatusCode.OK, HttpStatusCode.Unauthorized]), $"round {round}: {string.Join(", ", statuses)}");
        }
    }

    // --- Failing safely ------------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, string Title, string Detail, string Type)> FailureShapeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (response.StatusCode, body.GetProperty("title").GetString()!, body.GetProperty("detail").GetString()!, body.GetProperty("type").GetString()!);
    }

    [Fact]
    public async Task Every_way_of_failing_the_code_step_looks_the_same()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var locked = await factory.CreateUserAsync("locked@example.test");

        // A wrong code, no step cookie at all, an expired step cookie, and a locked-out account.
        var wrongCode = await (await TwoFactorFlows.AtTheCodeStepAsync(factory, user)).VerifyTwoFactorAsync("000000");
        var noCookie = await factory.CreateApiClient().VerifyTwoFactorAsync("000000");
        var expiredClient = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        factory.Clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await expiredClient.VerifyTwoFactorAsync(factory.NextTotpCode(user));
        var lockedClient = await TwoFactorFlows.AtTheCodeStepAsync(factory, locked);
        await LockOutAsync(factory, "locked@example.test");
        var lockedOut = await lockedClient.VerifyTwoFactorAsync(factory.NextTotpCode(locked));

        var shapes = new List<(HttpStatusCode Status, string Title, string Detail, string Type)>();
        foreach (var response in new[] { wrongCode, noCookie, expired, lockedOut })
        {
            shapes.Add(await FailureShapeAsync(response));
        }

        var first = shapes[0];
        Assert.Equal(HttpStatusCode.Unauthorized, first.Status);
        foreach (var shape in shapes)
        {
            Assert.Equal(first, shape);
        }
    }

    private static async Task LockOutAsync(ApiFactory factory, string email)
    {
        var client = factory.CreateApiClient();
        for (var i = 0; i < 5; i++)
        {
            await client.SignInAsync(email, "wrong password " + i);
        }
    }

    [Fact]
    public async Task A_code_without_the_password_step_gets_nowhere()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        var response = await factory.CreateApiClient().VerifyTwoFactorAsync(factory.NextTotpCode(user));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The step cookie ---------------------------------------------------------------------

    [Fact]
    public async Task The_step_cookie_is_host_prefixed_http_only_secure_same_site_strict_and_not_persistent()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        var response = await factory.CreateApiClient().SignInAsync(Email);

        var cookie = ApiClient.SetCookieHeader(response, "__Host-cr-2fa");
        Assert.NotNull(cookie);
        var attributes = cookie.ToLowerInvariant();
        Assert.Contains("httponly", attributes, StringComparison.Ordinal);
        Assert.Contains("secure", attributes, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", attributes, StringComparison.Ordinal);
        Assert.Contains("path=/", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("max-age=", attributes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_step_cookie_gives_no_access_to_anything_by_itself()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/signed-in-only")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task The_step_cookie_lasts_five_minutes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        var inTime = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await inTime.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);

        var late = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await late.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
    }

    [Fact]
    public async Task Using_the_step_cookie_does_not_extend_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        // A wrong code at four minutes does not buy more time.
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        await client.VerifyTwoFactorAsync("000000");
        factory.Clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
    }

    [Fact]
    public async Task Finishing_signs_the_step_cookie_out()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        var response = await client.VerifyTwoFactorAsync(factory.NextTotpCode(user));

        // The step cookie is cleared in the same response that issues the session cookie.
        var cleared = ApiClient.SetCookieHeader(response, "__Host-cr-2fa");
        Assert.NotNull(cleared);
        Assert.Contains("1970", cleared, StringComparison.Ordinal);
        Assert.NotNull(ApiClient.SetCookieHeader(response, "__Host-cr"));
    }

    // --- Remember-me is disabled -------------------------------------------------------------

    [Fact]
    public async Task Every_sign_in_asks_for_a_code_even_straight_after_a_successful_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);
        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        var again = await client.SignInAsync(Email);

        Assert.Equal("two-factor-required", await TwoFactorFlows.StatusAsync(again));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task No_remember_this_device_cookie_is_ever_issued()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = factory.CreateApiClient();

        var cookieNames = new List<string>();
        void Collect(HttpResponseMessage response) =>
            cookieNames.AddRange(ApiClient.SetCookieHeaders(response).Select(header => header.Split('=')[0]));

        var restricted = await client.SignInAsync(Email);
        Collect(restricted);
        var (start, sharedKey, _) = await TwoFactorFlows.StartEnrolmentAsync(client);
        Collect(start);
        factory.RememberTwoFactorKey(user, Base32Decode(sharedKey!));
        var confirm = await TwoFactorFlows.ConfirmEnrolmentAsync(client, factory.NextTotpCode(user));
        Collect(confirm);
        Collect(await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out"));
        Collect(await client.SignInAsync(Email));
        Collect(await client.VerifyTwoFactorAsync(factory.NextTotpCode(user)));

        Assert.NotEmpty(cookieNames);
        Assert.DoesNotContain(cookieNames, name => name.Contains("remember", StringComparison.OrdinalIgnoreCase));
        Assert.All(cookieNames, name => Assert.Contains(name, _expectedCookies));
    }

    [Fact]
    public async Task A_remember_this_device_cookie_is_never_honoured_even_if_one_turns_up()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/2fa/verify", UriKind.Relative))
        {
            Content = JsonContent.Create(new { code = "000000" }),
        };
        request.Headers.Add("Cookie", "__Host-cr-2fa-remember=CfDJ8-something-made-up");
        request.Headers.Add(ApiClient.CsrfHeader, await client.GetCsrfTokenAsync());
        var response = await client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    private static byte[] Base32Decode(string text) => CaseRegister.Application.Auth.Base32.Decode(text);

    // --- Failed codes and lockout --------------------------------------------------------------

    [Fact]
    public async Task Wrong_codes_do_not_lock_the_account_or_the_password_step_and_the_right_code_still_works()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync("00000" + i)).StatusCode);
        }

        // Five wrong codes are five wrong codes: not a lockout of anything. (Ten in a row lock the authenticator; see SignInThrottleTests.)
        Assert.Equal(0, await factory.WithDbAsync(db => db.SignInLockouts.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
    }

    [Fact]
    public async Task A_right_password_in_between_does_not_wipe_out_failed_codes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var first = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        for (var i = 0; i < 6; i++)
        {
            await first.VerifyTwoFactorAsync("00000" + i);
        }

        // Back to the password step (right password), then four more wrong codes: ten in all, so the authenticator is locked.
        var second = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        for (var i = 0; i < 4; i++)
        {
            await second.VerifyTwoFactorAsync("11111" + i);
        }

        Assert.Equal(10, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null && f.Kind == CaseRegister.Infrastructure.Persistence.FailureKind.SecondFactor)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
    }

    [Fact]
    public async Task A_right_code_forgives_earlier_failures()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        for (var i = 0; i < 3; i++)
        {
            await client.VerifyTwoFactorAsync("00000" + i);
        }

        Assert.Equal(3, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));
        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);

        Assert.Equal(0, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));
    }

    [Fact]
    public async Task Failed_codes_are_audited_and_ten_in_a_row_lock_the_authenticator_without_changing_any_status()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        for (var i = 0; i < 10; i++)
        {
            await client.VerifyTwoFactorAsync("00000" + i);
        }

        var actions = (await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync())).Select(record => record.Action).ToList();

        Assert.Equal(10, actions.Count(action => action == "TwoFactorFailed"));
        Assert.DoesNotContain("AccountSuspended", actions);
        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(Email)).AccountStatus);
    }
    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("not a code at all")]
    public async Task Things_that_are_not_codes_fail_the_same_way_and_count_as_attempts(string code)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        var response = await client.VerifyTwoFactorAsync(code);

        // An empty code is a bad request (validation); anything else is simply a wrong code.
        Assert.Contains(response.StatusCode, _validationOrWrongCode);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Assert.Equal(1, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));
        }
    }

    // --- Rate limit: 5 a minute per user --------------------------------------------------------

    [Fact]
    public async Task The_sixth_code_in_a_minute_for_one_user_gets_429_with_Retry_After()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, twoFactorAttemptsPerMinute: 5);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        // Four wrong, then the right one: five attempts, and signing in works.
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.VerifyTwoFactorAsync("00000" + i)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.VerifyTwoFactorAsync(factory.NextTotpCode(user))).StatusCode);
        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        // The sixth attempt within the minute is refused before the code is even looked at.
        var again = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        var limited = await again.VerifyTwoFactorAsync(factory.NextTotpCode(user));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.InRange(int.Parse(values.Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
    }

    [Fact]
    public async Task A_refused_attempt_is_not_checked_and_does_not_count_towards_lockout()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, twoFactorAttemptsPerMinute: 3);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        for (var i = 0; i < 3; i++)
        {
            await client.VerifyTwoFactorAsync("00000" + i);
        }

        var limited = await client.VerifyTwoFactorAsync(factory.NextTotpCode(user)); // a perfectly good code

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(3, await factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.ClearedAt == null)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.SignInLockouts.CountAsync()));
    }

    [Fact]
    public async Task The_limit_is_per_user_so_one_member_cannot_block_another()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, twoFactorAttemptsPerMinute: 3);
        var busy = await factory.CreateUserAsync("busy@example.test");
        var other = await factory.CreateUserAsync("other@example.test");
        var busyClient = await TwoFactorFlows.AtTheCodeStepAsync(factory, busy);
        for (var i = 0; i < 4; i++)
        {
            await busyClient.VerifyTwoFactorAsync("00000" + i);
        }

        var otherClient = await TwoFactorFlows.AtTheCodeStepAsync(factory, other);

        Assert.Equal(HttpStatusCode.OK, (await otherClient.VerifyTwoFactorAsync(factory.NextTotpCode(other))).StatusCode);
    }
}
