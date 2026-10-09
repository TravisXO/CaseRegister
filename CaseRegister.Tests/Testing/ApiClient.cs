using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CaseRegister.Tests.Testing;

/// <summary>
/// A browser-like client: it keeps cookies, talks https (the cookies are Secure and __Host-
/// prefixed) and fetches an anti-forgery token before each unsafe request, the way the front end will.
/// </summary>
public sealed class ApiClient
{
    public const string CsrfHeader = "X-CSRF-TOKEN";

    public ApiClient(ApiFactory factory)
    {
        Http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
    }

    public HttpClient Http { get; }

    public async Task<string> GetCsrfTokenAsync()
    {
        using var response = await Http.GetAsync(new Uri("/api/auth/csrf", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("token").GetString()!;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        bool withCsrfToken = true,
        string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (withCsrfToken && method != HttpMethod.Get)
        {
            request.Headers.Add(CsrfHeader, await GetCsrfTokenAsync());
        }

        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return await Http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? forwardedFor = null) =>
        SendAsync(HttpMethod.Get, path, forwardedFor: forwardedFor);

    public Task<HttpResponseMessage> SignInAsync(
        string email,
        string password = ApiFactory.Password,
        string? turnstileToken = FakeTurnstileVerifier.ValidToken,
        string? forwardedFor = null) =>
        SendAsync(HttpMethod.Post, "/api/auth/sign-in", new { email, password, turnstileToken }, forwardedFor: forwardedFor);

    /// <summary>Step two of signing in: the code. Only works right after the password step.</summary>
    public Task<HttpResponseMessage> VerifyTwoFactorAsync(string code, string? forwardedFor = null) =>
        SendAsync(HttpMethod.Post, "/api/auth/2fa/verify", new { code }, forwardedFor: forwardedFor);

    /// <summary>Both steps: the password, then a fresh authenticator code. Returns the answer to the second step.</summary>
    public async Task<HttpResponseMessage> SignInWithTwoFactorAsync(
        ApiFactory factory,
        ApplicationUser user,
        string password = ApiFactory.Password,
        string? forwardedFor = null)
    {
        var first = await SignInAsync(user.Email!, password, forwardedFor: forwardedFor);
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);

        return await VerifyTwoFactorAsync(factory.NextTotpCode(user), forwardedFor);
    }

    public Task<HttpResponseMessage> ChangePasswordAsync(string currentPassword, string newPassword, string twoFactorCode) =>
        SendAsync(HttpMethod.Post, "/api/auth/change-password", new { currentPassword, newPassword, twoFactorCode });

    public static IReadOnlyList<string> SetCookieHeaders(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    public static string? SetCookieHeader(HttpResponseMessage response, string cookieName) =>
        SetCookieHeaders(response).FirstOrDefault(header => header.StartsWith(cookieName + "=", StringComparison.Ordinal));
}
