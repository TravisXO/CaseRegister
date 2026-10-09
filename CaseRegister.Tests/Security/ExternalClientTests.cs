using System.Net;
using System.Text;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Security;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Security;

/// <summary>A handler that answers from a delegate and keeps every request it saw.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add((request, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

        return respond(request);
    }
}

public class HaveIBeenPwnedClientTests
{
    // SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8
    private const string Prefix = "5BAA6";
    private const string Suffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";

    private static HaveIBeenPwnedClient Client(StubHandler handler, CapturingLoggerProvider? logs = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(HaveIBeenPwnedClient.BaseAddress) };
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(logs ?? new CapturingLoggerProvider()));

        return new HaveIBeenPwnedClient(http, factory.CreateLogger<HaveIBeenPwnedClient>());
    }

    private static HttpResponseMessage Body(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task Only_the_first_five_characters_of_the_hash_are_sent()
    {
        var handler = new StubHandler(_ => Body("0018A45C4D1DEF81644B54AB7F969B88D65:1"));

        await Client(handler).CheckAsync("password");

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/range/{Prefix}", request.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, request.RequestUri.Query);

        // Neither the password nor any more of its hash leaves the server.
        var everythingSent = request.RequestUri.PathAndQuery + string.Join(' ', request.Headers.Select(h => h.Key + string.Join(',', h.Value)));
        Assert.DoesNotContain("password", everythingSent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Suffix[..6], everythingSent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Prefix + Suffix, everythingSent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Padding_is_requested_so_the_match_count_is_hidden()
    {
        var handler = new StubHandler(_ => Body(string.Empty));

        await Client(handler).CheckAsync("password");

        Assert.Equal("true", Assert.Single(handler.Requests).Request.Headers.GetValues("Add-Padding").Single());
    }

    [Fact]
    public async Task A_matching_suffix_means_the_password_is_breached()
    {
        var handler = new StubHandler(_ => Body($"0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n{Suffix}:9545824\r\n011053FD0102E94D6AE2F8B83D76FAF94F6:1"));

        Assert.Equal(BreachCheckResult.Breached, await Client(handler).CheckAsync("password"));
    }

    [Fact]
    public async Task The_suffix_match_ignores_letter_case()
    {
        var handler = new StubHandler(_ => Body($"{Suffix.ToLowerInvariant()}:12"));

        Assert.Equal(BreachCheckResult.Breached, await Client(handler).CheckAsync("password"));
    }

    [Fact]
    public async Task No_matching_suffix_means_not_breached()
    {
        var handler = new StubHandler(_ => Body("0018A45C4D1DEF81644B54AB7F969B88D65:1\r\n011053FD0102E94D6AE2F8B83D76FAF94F6:1"));

        Assert.Equal(BreachCheckResult.NotBreached, await Client(handler).CheckAsync("password"));
    }

    [Fact]
    public async Task Padding_lines_with_a_count_of_zero_are_not_matches()
    {
        var handler = new StubHandler(_ => Body($"{Suffix}:0\r\n0018A45C4D1DEF81644B54AB7F969B88D65:3"));

        Assert.Equal(BreachCheckResult.NotBreached, await Client(handler).CheckAsync("password"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task An_error_response_means_unavailable_and_is_logged(HttpStatusCode status)
    {
        var logs = new CapturingLoggerProvider();
        var handler = new StubHandler(_ => Body("nope", status));

        Assert.Equal(BreachCheckResult.Unavailable, await Client(handler, logs).CheckAsync("password"));

        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.DoesNotContain("password", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Prefix, warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_network_failure_means_unavailable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));

        Assert.Equal(BreachCheckResult.Unavailable, await Client(handler).CheckAsync("password"));
    }

    [Fact]
    public async Task A_timeout_means_unavailable()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));

        Assert.Equal(BreachCheckResult.Unavailable, await Client(handler).CheckAsync("password"));
    }

    [Fact]
    public async Task Cancelling_the_request_is_not_swallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var handler = new StubHandler(_ => Body(string.Empty));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).CheckAsync("password", cancelled.Token));
    }
}

public class TurnstileVerifierTests
{
    private static TurnstileVerifier Verifier(StubHandler handler, string secret = ApiFactory.TurnstileTestSecret)
    {
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider()));

        return new TurnstileVerifier(
            new HttpClient(handler),
            Options.Create(new TurnstileOptions { SecretKey = secret }),
            factory.CreateLogger<TurnstileVerifier>());
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task It_posts_the_secret_the_token_and_the_client_address_to_cloudflare()
    {
        var handler = new StubHandler(_ => Json("""{"success": true}"""));

        var verified = await Verifier(handler).VerifyAsync("the-token", "203.0.113.9");

        Assert.True(verified);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(TurnstileVerifier.VerifyUrl, request.RequestUri!.ToString());

        var form = System.Web.HttpUtility.ParseQueryString(body);
        Assert.Equal(ApiFactory.TurnstileTestSecret, form["secret"]);
        Assert.Equal("the-token", form["response"]);
        Assert.Equal("203.0.113.9", form["remoteip"]);
    }

    [Fact]
    public async Task The_client_address_is_left_out_when_unknown()
    {
        var handler = new StubHandler(_ => Json("""{"success": true}"""));

        await Verifier(handler).VerifyAsync("the-token", null);

        Assert.Null(System.Web.HttpUtility.ParseQueryString(Assert.Single(handler.Requests).Body)["remoteip"]);
    }

    [Fact]
    public async Task A_token_cloudflare_rejects_fails()
    {
        var handler = new StubHandler(_ => Json("""{"success": false, "error-codes": ["invalid-input-response"]}"""));

        Assert.False(await Verifier(handler).VerifyAsync("bad-token", "203.0.113.9"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_token_fails_without_calling_cloudflare(string? token)
    {
        var handler = new StubHandler(_ => Json("""{"success": true}"""));

        Assert.False(await Verifier(handler).VerifyAsync(token, "203.0.113.9"));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task An_error_response_fails_closed(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => Json("""{"success": true}""", status));

        Assert.False(await Verifier(handler).VerifyAsync("the-token", null));
    }

    [Fact]
    public async Task A_network_failure_fails_closed()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));

        Assert.False(await Verifier(handler).VerifyAsync("the-token", null));
    }

    [Fact]
    public async Task An_unreadable_answer_fails_closed()
    {
        var handler = new StubHandler(_ => Json("this is not json"));

        Assert.False(await Verifier(handler).VerifyAsync("the-token", null));
    }
}
