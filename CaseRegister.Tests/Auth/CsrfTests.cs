using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class CsrfTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    [Fact]
    public async Task The_csrf_endpoint_issues_a_token_and_a_locked_down_host_prefixed_cookie()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        var response = await client.GetAsync("/api/auth/csrf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));

        var cookie = ApiClient.SetCookieHeader(response, "__Host-csrf");
        Assert.NotNull(cookie);
        var attributes = cookie.ToLowerInvariant();
        Assert.Contains("httponly", attributes, StringComparison.Ordinal);
        Assert.Contains("secure", attributes, StringComparison.Ordinal);
        Assert.Contains("samesite=strict", attributes, StringComparison.Ordinal);
        Assert.Contains("path=/", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", attributes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_token_response_is_never_cached()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().GetAsync("/api/auth/csrf");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csrf_endpoint_needs_no_sign_in()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().GetAsync("/api/auth/csrf")).StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_unsafe_method_is_refused_without_a_token(string method)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        var response = await client.SendAsync(new HttpMethod(method), "/probe", withCsrfToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Every_unsafe_method_is_accepted_with_a_valid_token(string method)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        var response = await client.SendAsync(new HttpMethod(method), "/probe");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Safe_methods_need_no_token()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().GetAsync("/probe/ip")).StatusCode);
    }

    [Fact]
    public async Task A_made_up_token_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        await client.GetCsrfTokenAsync(); // the cookie exists, the header will be wrong

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/probe", UriKind.Relative));
        request.Headers.Add(ApiClient.CsrfHeader, "not-a-real-token");
        var response = await client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_matching_cookie_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var token = await factory.CreateApiClient().GetCsrfTokenAsync();

        // A different browser, with no csrf cookie, replaying the token it was somehow given.
        var stranger = factory.CreateApiClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/probe", UriKind.Relative));
        request.Headers.Add(ApiClient.CsrfHeader, token);
        var response = await stranger.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_cookie_alone_is_not_enough()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        await client.GetCsrfTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/probe", UriKind.Relative));
        var response = await client.Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_refusal_does_not_say_why()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().SendAsync(HttpMethod.Post, "/probe", withCsrfToken: false);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Invalid request", body.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/api/auth/sign-in")]
    [InlineData("/api/auth/sign-out")]
    [InlineData("/api/auth/change-password")]
    public async Task The_real_auth_endpoints_are_protected_too(string path)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        // Sign-out and change-password need a session to get past authentication; only then is the token checked.
        if (!path.EndsWith("sign-in", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);
        }

        var turnstileCallsBefore = factory.Turnstile.Calls.Count;
        var response = await client.SendAsync(
            HttpMethod.Post,
            path,
            new { email = Email, password = ApiFactory.Password, turnstileToken = FakeTurnstileVerifier.ValidToken, currentPassword = "a", newPassword = "b" },
            withCsrfToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(turnstileCallsBefore, factory.Turnstile.Calls.Count); // it never got as far as signing in
    }

    [Fact]
    public async Task A_token_fetched_before_signing_in_is_no_good_afterwards_so_the_front_end_refetches()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        var staleToken = await client.GetCsrfTokenAsync();
        await client.SignInWithTwoFactorAsync(factory, user);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/sign-out", UriKind.Relative));
        request.Headers.Add(ApiClient.CsrfHeader, staleToken);
        var response = await client.Http.SendAsync(request);

        // Tokens are tied to who is signed in, so an anonymous token cannot be replayed by a member session.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out")).StatusCode);
    }
}
