using System.Net;
using System.Text.Json;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;

namespace CaseRegister.Tests.Hardening;

/// <summary>Every error is a ProblemDetails with a trace id, and none of them tells a stranger how the server works inside.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class ErrorResponseTests(PostgresFixture postgres, NoDatabaseApp app) : IClassFixture<NoDatabaseApp>
{
    private static readonly string[] _neverShown =
    [
        "SELECT", "InvalidOperationException", "123456/78/9", "ProbeController", "CaseRegister.", "   at ", "stackTrace", "exception", "StackTrace",
    ];

    private async Task<HttpResponseMessage> PostJsonAsync(string path, string body)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        return await factory.CreateApiClient().SendContentAsync(HttpMethod.Post, path, HardeningHelpers.Json(body));
    }

    [Fact]
    public async Task An_unhandled_exception_is_a_plain_500_with_a_trace_id_and_nothing_from_the_exception()
    {
        using var client = app.Factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/probe/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.Equal("An error occurred while processing your request", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.False(problem.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        foreach (var secret in _neverShown)
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_trace_id_in_the_error_is_the_correlation_id_in_the_request_log()
    {
        using var client = app.Factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/probe/throw");

        var traceId = (await response.ProblemAsync()).GetProperty("traceId").GetString()!;
        Assert.Contains(app.Factory.LogOutput.Lines, line => line.Contains("\"Route\":\"probe/throw\"", StringComparison.Ordinal) && line.Contains($"\"CorrelationId\":\"{traceId}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_sign_in_is_a_problem_with_a_trace_id()
    {
        using var client = app.Factory.Https();

        using var unauthorised = await client.SendAsync(HttpMethod.Get, "/probe/members-only");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorised.StatusCode);
        var problem = await unauthorised.ProblemAsync();
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task A_missing_anti_forgery_token_is_a_problem_with_a_trace_id()
    {
        using var client = app.Factory.Https();

        using var response = await client.SendAsync(HttpMethod.Post, "/probe", HardeningHelpers.Json("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.ProblemAsync()).GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task A_field_that_fails_validation_is_a_400_with_the_field_names_and_a_trace_id()
    {
        // Sign-in has a validator that needs an email address, a password and a Turnstile token.
        using var response = await PostJsonAsync("/api/auth/sign-in", """{"email":"","password":"","turnstileToken":""}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.True(problem.GetProperty("errors").EnumerateObject().Any());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Theory]
    [InlineData("""{"name":"Ada","count":1,"extraThing":"value-from-the-client"}""", "extraThing")] // an unknown property
    [InlineData("""{"name":"Ada","count":"many-apples"}""", "many-apples")] // the wrong type
    [InlineData("""{"name":"Ada","count":""", "Ada")] // cut off
    [InlineData("""{"name":"Ada","count":1,"count":2,,}""", "Ada")] // not JSON
    [InlineData("""{"count":1}""", "ProbeBody")] // a missing field (a non-nullable string is required)
    public async Task A_body_the_framework_cannot_read_gets_one_generic_problem_that_echoes_nothing(string body, string neverEchoed)
    {
        using var response = await PostJsonAsync("/probe/json", body);

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.Equal("The request body is not valid.", problem.GetProperty("errors").GetProperty("body")[0].GetString());
        Assert.DoesNotContain(neverEchoed, text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BytePositionInLine", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_does_not_exist_gives_a_signed_out_caller_a_plain_401_and_nothing_more()
    {
        using var client = app.Factory.Https();

        using var get = await client.SendAsync(HttpMethod.Get, "/definitely/not/here");
        using var post = await client.SendAsync(HttpMethod.Post, "/api/not-a-thing", HardeningHelpers.Json("{}"));
        using var real = await client.SendAsync(HttpMethod.Get, "/probe/members-only");

        // The same answer whether or not the address exists, so the answer does not reveal which routes there are.
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, real.StatusCode);
        var text = await get.Content.ReadAsStringAsync();
        Assert.DoesNotContain("/definitely", text, StringComparison.Ordinal);
        Assert.DoesNotContain("endpoint", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft", text, StringComparison.Ordinal);
        Assert.False(get.Headers.Contains("WWW-Authenticate") && get.Headers.GetValues("WWW-Authenticate").Any(value => value.Contains("realm", StringComparison.OrdinalIgnoreCase)));
    }
}
