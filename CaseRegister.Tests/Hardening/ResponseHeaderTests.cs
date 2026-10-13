using System.Net;
using CaseRegister.Api.Hardening;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Hardening;

public sealed class ResponseHeaderTests(NoDatabaseApp app) : IClassFixture<NoDatabaseApp>
{
    private const string StrictPolicy = "default-src 'none'; frame-ancestors 'none'";

    [Theory]
    [InlineData("GET", "/probe/ip", 200)] // a success
    [InlineData("GET", "/health/live", 200)] // a health check
    [InlineData("GET", "/probe/members-only", 401)] // refused: not signed in
    [InlineData("GET", "/no/such/route", 401)] // not found, to someone not signed in
    [InlineData("POST", "/probe", 400)] // refused: no anti-forgery token
    [InlineData("GET", "/probe/throw", 500)] // an unhandled exception
    public async Task Every_response_carries_the_full_set_of_security_headers(string method, string path, int status)
    {
        using var client = app.Factory.Https();

        using var response = await client.SendAsync(new HttpMethod(method), path);

        Assert.Equal(status, (int)response.StatusCode);
        AssertSecurityHeaders(response);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal(StrictPolicy, Single(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, Single(response, "Permissions-Policy"));
        Assert.Equal("no-store", Single(response, "Cache-Control"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Resource-Policy"));

        // Nothing that names the server software.
        Assert.False(response.Headers.Contains("Server"));
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("X-AspNet-Version"));
    }

    private static string Single(HttpResponseMessage response, string name)
    {
        var found = response.Headers.TryGetValues(name, out var values) ? values : response.Content.Headers.GetValues(name);

        return Assert.Single(found);
    }

    [Fact]
    public void The_content_security_policy_is_the_strict_one_word_for_word()
    {
        Assert.Equal(StrictPolicy, SecurityHeadersMiddleware.ApiContentSecurityPolicy);
    }

    [Fact]
    public void Every_browser_feature_in_the_permissions_policy_is_switched_off()
    {
        var features = SecurityHeadersMiddleware.PermissionsPolicy.Split(", ");

        Assert.NotEmpty(features);
        Assert.All(features, feature => Assert.Matches(@"^[a-z-]+=\(\)$", feature));
        foreach (var important in new[] { "camera", "microphone", "geolocation", "payment", "usb", "fullscreen", "serial" })
        {
            Assert.Contains($"{important}=()", features);
        }
    }

    [Fact]
    public async Task Strict_transport_security_lasts_a_year_and_covers_subdomains_outside_development()
    {
        using var client = app.Factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/health/live");

        Assert.Equal("max-age=31536000; includeSubDomains", Single(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task Strict_transport_security_is_not_sent_over_plain_http()
    {
        using var client = app.Factory.Http();

        using var response = await client.SendAsync(HttpMethod.Get, "/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public void The_server_is_set_to_hide_itself_and_to_the_limits_stated_in_code()
    {
        var kestrel = app.Factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        Assert.False(kestrel.AddServerHeader);
        Assert.Equal(65_536, kestrel.Limits.MaxRequestBodySize);
        Assert.Equal(16_384, kestrel.Limits.MaxRequestHeadersTotalSize);
        Assert.Equal(50, kestrel.Limits.MaxRequestHeaderCount);
        Assert.Equal(4_096, kestrel.Limits.MaxRequestLineSize);
        Assert.Equal(TimeSpan.FromSeconds(10), kestrel.Limits.RequestHeadersTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), kestrel.Limits.KeepAliveTimeout);
    }

    [Fact]
    public async Task No_cross_origin_access_is_ever_granted_not_even_to_a_preflight_request()
    {
        using var client = app.Factory.Https();

        foreach (var (method, path) in new[] { ("OPTIONS", "/probe/ip"), ("OPTIONS", "/api/auth/sign-in"), ("GET", "/probe/ip"), ("OPTIONS", "/health/live") })
        {
            using var response = await client.SendAsync(
                new HttpMethod(method),
                path,
                content: null,
                ("Origin", "https://evil.example"),
                ("Access-Control-Request-Method", "POST"),
                ("Access-Control-Request-Headers", "content-type"));

            Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
            Assert.False(method == "OPTIONS" && response.IsSuccessStatusCode && path != "/health/live", $"{path} answered a preflight request with success");
        }
    }

    [Fact]
    public void The_limits_have_the_values_the_code_says()
    {
        Assert.Equal(64 * 1024, RequestLimits.DefaultBodyBytes);
        Assert.Equal(8 * 1024, RequestLimits.AuthBodyBytes);
        Assert.True(RequestLimits.AuthBodyBytes < RequestLimits.DefaultBodyBytes);
        Assert.Equal(16, RequestLimits.MaxJsonDepth);
        Assert.Equal(TimeSpan.FromSeconds(30), RequestLimits.RequestTimeout);
    }
}
