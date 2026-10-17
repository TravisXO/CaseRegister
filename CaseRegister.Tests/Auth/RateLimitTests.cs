using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Api.Security;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class RateLimitTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private static int RetryAfterSeconds(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Retry-After", out var values), "429 must carry a Retry-After header");

        return int.Parse(values.Single(), CultureInfo.InvariantCulture);
    }

    // --- Per IP: 5 a minute ------------------------------------------------------------------

    [Fact]
    public async Task The_sixth_sign_in_in_a_minute_from_one_address_gets_429_with_Retry_After()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        var client = factory.CreateApiClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync("nobody@example.test", forwardedFor: "203.0.113.1")).StatusCode);
        }

        var limited = await client.SignInAsync("nobody@example.test", forwardedFor: "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(RetryAfterSeconds(limited), 1, 60);
        var body = await limited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(429, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_refused_attempt_is_not_processed_at_all()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await client.SignInAsync(Email, "wrong password", forwardedFor: "203.0.113.1");
        }

        var hashesBefore = factory.PasswordVerifications.Count;
        var turnstileBefore = factory.Turnstile.Calls.Count;

        // Even the right password, from the limited address, is turned away before any work is done.
        var limited = await client.SignInAsync(Email, forwardedFor: "203.0.113.1");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(hashesBefore, factory.PasswordVerifications.Count);
        Assert.Equal(turnstileBefore, factory.Turnstile.Calls.Count);
        Assert.Null(ApiClient.SetCookieHeader(limited, "__Host-cr"));
    }

    [Fact]
    public async Task Successful_sign_ins_count_towards_the_per_address_limit_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SignInAsync(Email, forwardedFor: "203.0.113.1")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SignInAsync(Email, forwardedFor: "203.0.113.1")).StatusCode);
    }

    [Fact]
    public async Task One_address_being_limited_does_not_affect_another()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        var client = factory.CreateApiClient();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await client.SignInAsync("nobody@example.test", forwardedFor: "203.0.113.1");
        }

        var other = await client.SignInAsync("nobody@example.test", forwardedFor: "203.0.113.2");

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task Other_endpoints_are_not_affected_by_the_sign_in_limit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        var client = factory.CreateApiClient();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await client.SignInAsync("nobody@example.test", forwardedFor: "203.0.113.1");
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/csrf", forwardedFor: "203.0.113.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/ip", forwardedFor: "203.0.113.1")).StatusCode);
    }

    // --- Per account and address: 10 an hour -----------------------------------------------------

    [Fact]
    public async Task The_eleventh_sign_in_for_one_account_from_one_address_in_an_hour_gets_429_but_another_address_is_not_affected()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerAccountPerHour: 10);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        // One address using up its attempts at the account (the lockout rules are separate and have their own tests).
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var response = await client.SignInAsync(Email, "wrong password " + attempt, forwardedFor: "198.51.100.1");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.SignInAsync(Email, forwardedFor: "198.51.100.1");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(RetryAfterSeconds(limited), 1, 3600);

        // The attacker's attempts did not use up the account owner's: they sign in from their own address.
        var owner = await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user, forwardedFor: "203.0.113.20");
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Fact]
    public async Task The_per_account_limit_applies_to_emails_that_do_not_exist_so_it_reveals_nothing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerAccountPerHour: 10);
        var client = factory.CreateApiClient();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await client.SignInAsync("ghost@example.test", forwardedFor: "198.51.100.1");
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.SignInAsync("ghost@example.test", forwardedFor: "198.51.100.1")).StatusCode);
    }

    [Fact]
    public async Task Capitals_and_spaces_do_not_dodge_the_per_account_limit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerAccountPerHour: 10);
        var client = factory.CreateApiClient();
        var variants = new[] { "ghost@example.test", "GHOST@example.test", " Ghost@Example.test ", "ghost@EXAMPLE.TEST", "gHoSt@example.test" };

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await client.SignInAsync(variants[attempt % variants.Length], forwardedFor: "198.51.100.1");
        }

        Assert.Equal(
            HttpStatusCode.TooManyRequests,
            (await client.SignInAsync(" GHOST@example.test", forwardedFor: "198.51.100.1")).StatusCode);
    }

    [Fact]
    public async Task One_account_being_limited_does_not_affect_another()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerAccountPerHour: 10);
        var client = factory.CreateApiClient();
        for (var attempt = 1; attempt <= 11; attempt++)
        {
            await client.SignInAsync("ghost@example.test", forwardedFor: "198.51.100.1");
        }

        var other = await client.SignInAsync("someone.else@example.test", forwardedFor: "198.51.100.1");

        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public void The_account_limiter_allows_ten_then_says_how_long_to_wait_and_counts_each_address_separately()
    {
        using var limiter = new SignInAccountLimiter(Options.Create(new RateLimitingOptions { SignInPerAccountPerHour = 10 }));

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.True(limiter.TryAcquire("a@example.test", "198.51.100.1", out _));
        }

        Assert.False(limiter.TryAcquire("a@example.test", "198.51.100.1", out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        Assert.True(limiter.TryAcquire("b@example.test", "198.51.100.1", out _));
        Assert.True(limiter.TryAcquire("a@example.test", "198.51.100.2", out _)); // the same account from another address has its own ten
    }
    // --- Real client addresses ---------------------------------------------------------------

    [Fact]
    public async Task The_address_the_app_sees_is_the_one_the_proxy_reports()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        var response = await client.GetAsync("/probe/ip", forwardedFor: "198.51.100.7");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("198.51.100.7", body.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Only_the_last_forwarded_hop_is_believed_so_a_client_cannot_spoof_its_address()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        // The client claims to be 1.2.3.4. The proxy in front of the app appended the real address after it.
        var response = await client.GetAsync("/probe/ip", forwardedFor: "1.2.3.4, 198.51.100.7");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("198.51.100.7", body.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Spoofing_an_earlier_hop_does_not_buy_a_fresh_per_address_limit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 5);
        var client = factory.CreateApiClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await client.SignInAsync("nobody@example.test", forwardedFor: $"10.0.0.{attempt}, 198.51.100.7");
        }

        var response = await client.SignInAsync("nobody@example.test", forwardedFor: "10.0.0.99, 198.51.100.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }
}
