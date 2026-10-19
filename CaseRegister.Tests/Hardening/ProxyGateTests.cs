using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Hardening;

/// <summary>One application started as Production (no database needed), shared by the tests that only send requests.</summary>
public sealed class ProductionApp : IDisposable
{
    public ProductionApp() => Factory = ApiFactory.WithoutDatabase(environmentName: "Production");

    public ApiFactory Factory { get; }

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// Only the proxy may reach the API. Every request but the liveness check must carry the secret the Pages Function adds, and the visitor's
/// address is believed only on a request that did.
/// </summary>
public sealed class ProxyGateTests(ProductionApp app) : IClassFixture<ProductionApp>
{
    private const string Secret = ApiFactory.ProxyTestSecret;

    /// <summary>A client that sends nothing of its own: not the one the factory hands out, which already carries the secret.</summary>
    private HttpClient Bare()
    {
        var client = app.Factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        return client;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, params (string Name, string Value)[] headers) =>
        client.SendAsync(method, path, content: null, headers);

    private static async Task<string?> IpSeenAsync(HttpClient client, params (string Name, string Value)[] headers)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", headers);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ip").GetString();
    }

    // --- Without the secret, nothing -------------------------------------------------------------------------

    [Theory]
    [InlineData("GET", "/probe/ip")]
    [InlineData("POST", "/probe")]
    [InlineData("GET", "/api/auth/csrf")]
    [InlineData("GET", "/health/ready")]
    [InlineData("GET", "/does/not/exist")]
    [InlineData("GET", "/health/liveness")]
    [InlineData("POST", "/health/live")]
    [InlineData("DELETE", "/health/live")]
    public async Task A_request_without_the_secret_is_refused_whatever_it_asks_for(string method, string path)
    {
        using var client = Bare();

        using var response = await SendAsync(client, new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task The_refusal_says_nothing_about_why_or_what_exists_and_still_carries_the_security_headers()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/api/auth/csrf");
        var text = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proxy", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("default-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task The_liveness_check_alone_needs_no_secret()
    {
        using var client = Bare();

        using var get = await SendAsync(client, HttpMethod.Get, "/health/live");
        using var head = await SendAsync(client, HttpMethod.Head, "/health/live");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("Healthy", await get.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    [InlineData("proxy-secret-for-tests-0123456789-abcdeX")] // the right length, one character off
    [InlineData("proxy-secret-for-tests-0123456789-abcdef ")] // trailing space
    [InlineData("PROXY-SECRET-FOR-TESTS-0123456789-ABCDEF")] // case matters
    public async Task A_wrong_secret_is_refused_like_none_at_all(string sent)
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, sent));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Two_secret_headers_are_refused_even_if_one_is_right()
    {
        using var client = Bare();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/probe/ip", UriKind.Relative));
        request.Headers.Add(ApiFactory.ProxyHeader, [Secret, "another"]);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_right_secret_gets_through()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, Secret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_secret_is_never_taken_from_a_query_string_a_cookie_or_another_header()
    {
        using var client = Bare();

        using var query = await SendAsync(client, HttpMethod.Get, $"/probe/ip?{ApiFactory.ProxyHeader}={Secret}");
        using var cookie = await SendAsync(client, HttpMethod.Get, "/probe/ip", ("Cookie", $"{ApiFactory.ProxyHeader}={Secret}"));
        using var authorization = await SendAsync(client, HttpMethod.Get, "/probe/ip", ("Authorization", $"Bearer {Secret}"));

        Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, cookie.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, authorization.StatusCode);
    }

    // --- Rotation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task During_a_rotation_both_the_current_and_the_next_secret_work_and_nothing_else_does()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Proxy:NextSecret"] = ApiFactory.NextProxyTestSecret, environmentName: "Production");
        using var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var current = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, ApiFactory.ProxyTestSecret));
        using var next = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, ApiFactory.NextProxyTestSecret));
        using var other = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, "x" + ApiFactory.NextProxyTestSecret));

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [Fact]
    public async Task With_no_next_secret_the_next_slot_accepts_nothing()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, ApiFactory.NextProxyTestSecret));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- The visitor's address ---------------------------------------------------------------------------------

    [Fact]
    public async Task On_a_request_that_passed_the_gate_the_visitors_address_is_the_one_the_proxy_sent()
    {
        using var client = Bare();

        Assert.Equal("203.0.113.9", await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, "203.0.113.9")));
        Assert.Equal("2001:db8::7", await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, "2001:db8::7")));
    }

    [Fact]
    public async Task A_spoofed_address_without_the_secret_is_never_read_the_request_is_refused()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ProxyOptions.ClientIpHeaderName, "203.0.113.9"), ("X-Forwarded-For", "203.0.113.9"), ("CF-Connecting-IP", "203.0.113.9"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("1")] // .NET reads this as 0.0.0.1
    [InlineData("203.0.113.9, 198.51.100.1")]
    [InlineData("203.0.113.9:443")]
    [InlineData("999.1.1.1")]
    [InlineData("")]
    public async Task An_address_that_is_not_one_clean_address_is_ignored_and_the_connections_own_stands(string sent)
    {
        using var client = Bare();
        var own = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret));

        var seen = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, sent));

        Assert.Equal(own, seen);
    }

    [Fact]
    public async Task Two_address_headers_are_ignored_rather_than_picking_one()
    {
        using var client = Bare();
        var own = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/probe/ip", UriKind.Relative));
        request.Headers.Add(ApiFactory.ProxyHeader, Secret);
        request.Headers.Add(ProxyOptions.ClientIpHeaderName, ["203.0.113.9", "198.51.100.1"]);

        using var response = await client.SendAsync(request);

        Assert.Equal(own, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ip").GetString());
    }

    [Fact]
    public async Task A_forwarded_for_chain_a_visitor_made_up_does_not_choose_the_address_and_the_proxys_word_wins_over_it()
    {
        using var client = Bare();

        // Render appends the address that connected to it; only that last one is believed (ForwardLimit 1), and then the proxy's word replaces it.
        var withoutProxyWord = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), ("X-Forwarded-For", "6.6.6.6, 7.7.7.7"));
        var withProxyWord = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), ("X-Forwarded-For", "6.6.6.6, 7.7.7.7"), (ProxyOptions.ClientIpHeaderName, "203.0.113.9"));

        Assert.Equal("7.7.7.7", withoutProxyWord);
        Assert.Equal("203.0.113.9", withProxyWord);
    }

    [Fact]
    public async Task A_cf_connecting_ip_header_sent_to_the_api_is_not_trusted_at_all()
    {
        using var client = Bare();
        var own = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret));

        var seen = await IpSeenAsync(client, (ApiFactory.ProxyHeader, Secret), ("CF-Connecting-IP", "203.0.113.9"));

        Assert.Equal(own, seen);
    }

    [Fact]
    public async Task Each_visitor_gets_a_rate_limit_of_their_own_instead_of_all_sharing_the_proxys_address()
    {
        using var factory = ApiFactory.WithoutDatabase(environmentName: "Production", globalPerIpPerMinute: 3);
        using var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        for (var i = 0; i < 3; i++)
        {
            using var allowed = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, "203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var tooMany = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, "203.0.113.1"));
        using var someoneElse = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, Secret), (ProxyOptions.ClientIpHeaderName, "203.0.113.2"));

        Assert.Equal(HttpStatusCode.TooManyRequests, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.OK, someoneElse.StatusCode);
    }

    // --- The visitor's city and country ---------------------------------------------------------------------------

    private static async Task<(string? Country, string? City)> GeoSeenAsync(HttpClient client, params (string Name, string Value)[] headers)
    {
        using var response = await SendAsync(client, HttpMethod.Get, "/probe/geo", headers);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (body.GetProperty("country").GetString(), body.GetProperty("city").GetString());
    }

    [Fact]
    public async Task On_a_request_that_passed_the_gate_the_city_and_country_are_the_ones_the_proxy_sent()
    {
        using var client = Bare();

        Assert.Equal(("ZM", "Lusaka"), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "ZM|Lusaka")));
        Assert.Equal(("BR", "São Paulo"), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "BR|S%C3%A3o%20Paulo")));
        Assert.Equal(("ZM", null), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "ZM|")));
    }

    [Fact]
    public async Task Without_the_secret_a_spoofed_place_is_never_read_the_request_is_refused()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/geo", (ClientLocation.HeaderName, "RU|Moscow"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Moscow", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_a_wrong_secret_a_spoofed_place_is_refused_too()
    {
        using var client = Bare();

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/geo", (ApiFactory.ProxyHeader, "x" + Secret), (ClientLocation.HeaderName, "RU|Moscow"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    [InlineData("ZMB|Lusaka")]
    [InlineData("zm|Lusaka")]
    [InlineData("12|Lusaka")]
    [InlineData("|Lusaka")]
    [InlineData("Lusaka")]
    [InlineData("<script>|x")]
    public async Task A_place_that_is_not_a_clean_country_and_city_is_ignored(string sent)
    {
        using var client = Bare();

        Assert.Equal((null, null), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, sent)));
    }

    [Fact]
    public async Task Control_characters_and_undecodable_bytes_in_the_city_are_dropped_and_a_long_city_is_cut()
    {
        using var client = Bare();

        Assert.Equal(("ZM", "Lusaka"), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "ZM|Lu%00sa%0D%0Aka")));
        Assert.Equal(("ZM", null), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "ZM|%FF%FE")));
        Assert.Equal(("ZM", new string('a', ClientLocation.MaxCityLength)), await GeoSeenAsync(client, (ApiFactory.ProxyHeader, Secret), (ClientLocation.HeaderName, "ZM|" + new string('a', 300))));
    }

    [Fact]
    public async Task Two_place_headers_are_ignored_rather_than_picking_one()
    {
        using var client = Bare();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/probe/geo", UriKind.Relative));
        request.Headers.Add(ApiFactory.ProxyHeader, Secret);
        request.Headers.Add(ClientLocation.HeaderName, ["ZM|Lusaka", "RU|Moscow"]);

        using var response = await client.SendAsync(request);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("country").ValueKind);
    }

    [Fact]
    public async Task Where_there_is_no_proxy_secret_at_all_the_place_header_is_not_believed_either()
    {
        using var factory = ApiFactory.WithoutDatabase(); // the gate is off here: nothing vouches for the header
        using var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        Assert.Equal((null, null), await GeoSeenAsync(client, (ClientLocation.HeaderName, "RU|Moscow")));
    }

    // --- What gets logged ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_is_logged_without_the_secret_that_was_sent_or_the_real_one()
    {
        const string sentSecret = "a-guess-that-must-never-be-logged-a-guess-that-must-never-be-logged";
        using var factory = ApiFactory.WithoutDatabase(environmentName: "Production");
        using var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var response = await SendAsync(client, HttpMethod.Get, "/probe/ip", (ApiFactory.ProxyHeader, sentSecret));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var written = string.Join('\n', factory.LogOutput.Lines.Concat(factory.Logs.Entries.Select(entry => entry.Message)));
        Assert.Contains("did not carry the proxy secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain(sentSecret, written, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
    }

    // --- The settings -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", null, null, false)]
    [InlineData("Production", "", null, false)]
    [InlineData("Production", "short", null, false)]
    [InlineData("Production", ApiFactory.ProxyTestSecret, null, true)]
    [InlineData("Production", ApiFactory.ProxyTestSecret, ApiFactory.NextProxyTestSecret, true)]
    [InlineData("Production", ApiFactory.ProxyTestSecret, "short", false)]
    [InlineData("Production", ApiFactory.ProxyTestSecret, ApiFactory.ProxyTestSecret, false)]
    [InlineData("Production", null, ApiFactory.NextProxyTestSecret, false)] // the current secret must stay while the next one is introduced
    [InlineData("Development", null, null, true)] // the gate is simply off on a developer's machine
    [InlineData("Testing", null, null, true)]
    [InlineData("Development", "short", null, false)] // but a secret that is set has to be a real one
    public void The_settings_are_checked_at_startup(string environment, string? secret, string? next, bool valid)
    {
        var validator = new ProxyOptionsValidator(new FakeHostEnvironment(environment));

        var result = validator.Validate(null, new ProxyOptions { Secret = secret, NextSecret = next });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            // Names the setting, never a value.
            Assert.Contains("Proxy:", result.FailureMessage, StringComparison.Ordinal);
            foreach (var value in new[] { secret, next }.OfType<string>().Where(value => value.Length > 5))
            {
                Assert.DoesNotContain(value, result.FailureMessage, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Production_refuses_to_start_without_the_secret_and_never_shows_the_other_secrets()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove("Proxy:Secret"), environmentName: "Production");

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("Proxy:Secret", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.TestKeyBase64, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_secret_is_read_from_the_environment_style_name()
    {
        using var factory = ApiFactory.WithoutDatabase(environmentName: "Production");

        var options = factory.Services.GetRequiredService<IOptions<ProxyOptions>>().Value;

        Assert.Equal(Secret, options.Secret);
        Assert.True(options.Enabled);
    }
}
