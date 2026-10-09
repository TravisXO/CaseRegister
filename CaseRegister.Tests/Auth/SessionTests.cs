using System.Net;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class SessionTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private async Task<(ApiFactory Factory, ApiClient Client, HttpResponseMessage SignIn, ApplicationUser User)> SignedInAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        var response = await client.SignInWithTwoFactorAsync(factory, user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (factory, client, response, user);
    }

    private static async Task<HttpStatusCode> SessionStatusAsync(ApiClient client) =>
        (await client.GetAsync("/api/auth/session")).StatusCode;

    // --- The cookie itself -------------------------------------------------------------------

    [Fact]
    public async Task The_session_cookie_is_host_prefixed_http_only_secure_and_same_site_strict()
    {
        var (factory, _, response, _) = await SignedInAsync();
        await using var _ = factory;

        var cookie = ApiClient.SetCookieHeader(response, "__Host-cr");

        Assert.NotNull(cookie);
        var attributes = cookie.ToLowerInvariant();
        Assert.Contains("httponly", attributes, StringComparison.Ordinal);
        Assert.Contains("secure", attributes, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", attributes, StringComparison.Ordinal);
        Assert.Contains("path=/", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", attributes, StringComparison.Ordinal); // __Host- forbids it
    }

    [Fact]
    public async Task The_session_cookie_is_never_persistent()
    {
        var (factory, _, response, _) = await SignedInAsync();
        await using var _ = factory;

        var attributes = ApiClient.SetCookieHeader(response, "__Host-cr")!.ToLowerInvariant();

        // A session cookie has neither: it ends when the browser closes.
        Assert.DoesNotContain("expires=", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("max-age=", attributes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_other_cookie_is_set_by_signing_in()
    {
        var (factory, _, response, _) = await SignedInAsync();
        await using var _ = factory;

        var names = ApiClient.SetCookieHeaders(response).Select(header => header.Split('=')[0]).ToList();

        Assert.DoesNotContain(names, name => !name.StartsWith("__Host-", StringComparison.Ordinal));
    }

    // --- Idle timeout and sliding expiration -------------------------------------------------

    [Fact]
    public async Task The_session_ends_after_30_idle_minutes()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
    }

    [Fact]
    public async Task The_session_is_still_open_just_inside_the_idle_limit()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        factory.Clock.Advance(TimeSpan.FromMinutes(29));

        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
    }

    [Fact]
    public async Task Activity_keeps_the_session_alive_well_past_30_minutes()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        // 20 minutes between requests, for two hours: never idle for 30.
        for (var i = 0; i < 6; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
        }
    }

    // --- Absolute limit ----------------------------------------------------------------------

    [Fact]
    public async Task However_active_the_user_is_the_session_ends_8_hours_after_signing_in()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        // Busy all day: a request every 20 minutes, so the idle timeout never fires.
        var elapsed = TimeSpan.Zero;
        while (elapsed + TimeSpan.FromMinutes(20) < TimeSpan.FromHours(8))
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            elapsed += TimeSpan.FromMinutes(20);
            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
        }

        // 7 hours 40 minutes in and still fine. The next step reaches exactly 8 hours.
        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
    }

    [Fact]
    public async Task After_the_absolute_limit_the_member_can_sign_in_again_for_a_fresh_8_hours()
    {
        var (factory, client, _, user) = await SignedInAsync();
        await using var _ = factory;
        factory.Clock.Advance(TimeSpan.FromHours(8));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));

        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);

        // A fresh 8 hours: still open after 7 hours of steady activity (a request every 20 minutes).
        for (var i = 0; i < 21; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));
        }
    }

    // --- 401 and 403 instead of redirects ----------------------------------------------------

    [Fact]
    public async Task An_anonymous_request_to_a_protected_endpoint_gets_401_and_no_redirect()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        var response = await client.GetAsync("/probe/members-only");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_protected_post_without_a_session_gets_401_and_no_redirect()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().ChangePasswordAsync("whatever it was", "whatever it will be", "123456");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_signed_in_member_refused_by_a_policy_gets_403_and_no_redirect()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.GetAsync("/probe/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_anonymous_request_to_a_policy_protected_endpoint_gets_401_not_403()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().GetAsync("/probe/forbidden");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Security stamp ----------------------------------------------------------------------

    [Fact]
    public async Task A_changed_security_stamp_ends_the_session_but_only_at_the_next_5_minute_check()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.UpdateSecurityStampAsync((await users.FindByEmailAsync(Email))!);
        }

        // Inside the 5-minute window the stamp is not looked at again.
        factory.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, await SessionStatusAsync(client));

        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
    }

    [Fact]
    public async Task A_session_whose_account_is_no_longer_approved_ends_at_the_next_check_only_if_the_stamp_changed()
    {
        // Suspension changes the stamp (see the sign-in tests); this confirms the session really dies with it.
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByEmailAsync(Email))!;
            user.AccountStatus = Domain.Accounts.AccountStatus.Suspended;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
    }

    // --- Signing out -------------------------------------------------------------------------

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var (factory, client, _, _) = await SignedInAsync();
        await using var _ = factory;

        var response = await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await SessionStatusAsync(client));
    }
}
