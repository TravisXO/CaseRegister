using System.Net;
using System.Text;
using System.Text.Json;
using CaseRegister.Api.Hardening;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Hardening;

/// <summary>What a request may carry and how long it may take.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RequestLimitTests(PostgresFixture postgres)
{
    private static string JsonOfSize(int bytes) => "{\"v\":\"" + new string('a', bytes - 8) + "\"}";

    private static string Nested(int depth) => new string('[', depth) + new string(']', depth);

    // --- Body size -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_body_over_the_default_limit_is_refused_with_413_and_one_just_under_it_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var tooBig = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", HardeningHelpers.Json(JsonOfSize(65_537)));
        using var fits = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", HardeningHelpers.Json(JsonOfSize(65_536)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        var problem = await tooBig.ProblemAsync();
        Assert.Equal(413, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.Equal(HttpStatusCode.OK, fits.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/sign-in")]
    [InlineData("/api/auth/2fa/verify")]
    [InlineData("/api/auth/sign-up")]
    [InlineData("/api/auth/staff-setup/start")]
    [InlineData("/api/auth/staff-setup/complete")]
    public async Task The_authentication_endpoints_accept_only_a_much_smaller_body(string path)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var tooBig = await client.SendContentAsync(HttpMethod.Post, path, HardeningHelpers.Json(JsonOfSize(8_193)));
        using var fits = await client.SendContentAsync(HttpMethod.Post, path, HardeningHelpers.Json(JsonOfSize(8_000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, fits.StatusCode); // refused for what it says, not for its size
    }

    [Fact]
    public async Task An_endpoint_can_set_its_own_limit_smaller_or_larger()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var smallFits = await client.SendContentAsync(HttpMethod.Post, "/probe/limited", HardeningHelpers.Json("{\"v\":\"" + new string('a', 20) + "\"}"));
        using var smallTooBig = await client.SendContentAsync(HttpMethod.Post, "/probe/limited", HardeningHelpers.Json("{\"v\":\"" + new string('a', 40) + "\"}"));
        using var largeFits = await client.SendContentAsync(HttpMethod.Post, "/probe/generous", HardeningHelpers.Json(JsonOfSize(200_000)));
        using var largeTooBig = await client.SendContentAsync(HttpMethod.Post, "/probe/generous", HardeningHelpers.Json(JsonOfSize(1_000_001)));

        Assert.Equal(HttpStatusCode.OK, smallFits.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, smallTooBig.StatusCode);
        Assert.Equal(HttpStatusCode.OK, largeFits.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, largeTooBig.StatusCode);
    }

    [Fact]
    public async Task A_body_sent_in_chunks_with_no_declared_length_is_cut_off_at_the_limit_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        var bytes = Encoding.UTF8.GetBytes(JsonOfSize(100_000));
        using var content = new StreamContent(new NoLengthStream(bytes));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>A stream that cannot say how long it is, so the request has no Content-Length.</summary>
    private sealed class NoLengthStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 4096));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // --- What a body must be ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("application/xml")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/jsonx")]
    [InlineData("text/json")]
    public async Task A_body_that_is_not_json_is_refused_with_415(string contentType)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        using var content = new StringContent("{\"name\":\"Ada\",\"count\":1}");
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await client.SendContentAsync(HttpMethod.Post, "/probe/json", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await response.ProblemAsync()).GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task A_body_with_no_content_type_at_all_is_refused_with_415()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"name\":\"Ada\",\"count\":1}"));

        using var response = await client.SendContentAsync(HttpMethod.Post, "/probe/json", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public async Task A_json_body_is_accepted(string contentType)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();
        using var content = new StringContent("{\"name\":\"Ada\",\"count\":1}");
        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await client.SendContentAsync(HttpMethod.Post, "/probe/json", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_request_with_no_body_needs_no_content_type()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var response = await client.SendAsync(HttpMethod.Post, "/probe");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Json_nested_deeper_than_the_limit_is_refused_and_json_within_it_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var within = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", HardeningHelpers.Json(Nested(RequestLimits.MaxJsonDepth)));
        using var tooDeep = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", HardeningHelpers.Json(Nested(RequestLimits.MaxJsonDepth + 1)));
        using var far = await client.SendContentAsync(HttpMethod.Post, "/probe/anything", HardeningHelpers.Json(Nested(500)));

        Assert.Equal(HttpStatusCode.OK, within.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooDeep.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, far.StatusCode);
    }

    [Fact]
    public async Task The_json_settings_refuse_unknown_properties_and_set_a_depth()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var json = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;

        Assert.Equal(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, json.UnmappedMemberHandling);
        Assert.Equal(16, json.MaxDepth);
    }

    [Fact]
    public async Task A_known_endpoint_refuses_a_body_with_an_extra_property_and_accepts_the_same_body_without_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        using var extra = await client.SendContentAsync(HttpMethod.Post, "/probe/json", HardeningHelpers.Json("{\"name\":\"Ada\",\"count\":1,\"admin\":true}"));
        using var plain = await client.SendContentAsync(HttpMethod.Post, "/probe/json", HardeningHelpers.Json("{\"name\":\"Ada\",\"count\":1}"));

        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
    }

    // --- Time ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_that_takes_too_long_is_ended_with_504_and_a_problem()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, requestTimeout: TimeSpan.FromMilliseconds(500));
        using var client = factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/probe/slow");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.Equal(504, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task The_real_timeout_is_thirty_seconds_and_applies_to_every_request_by_default()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var policy = factory.Services.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value.DefaultPolicy;

        Assert.NotNull(policy);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Timeout);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, policy.TimeoutStatusCode);
    }

    // --- The fallback rate limit -----------------------------------------------------------------------------

    [Fact]
    public async Task The_global_limit_stops_a_client_that_asks_too_often_for_anything_and_leaves_health_checks_alone()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, globalPerIpPerMinute: 3);
        using var client = factory.Https();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.SendAsync(HttpMethod.Get, attempt % 2 == 0 ? "/probe/ip" : "/not/a/route");
            statuses.Add(response.StatusCode);
        }

        // Every kind of request counts: two real ones and a missing address got through, the fourth did not.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);

        using var limited = await client.SendAsync(HttpMethod.Get, "/probe/ip");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        Assert.False(string.IsNullOrEmpty((await limited.ProblemAsync()).GetProperty("traceId").GetString()));
        Assert.Equal(SecurityHeadersMiddleware.ApiContentSecurityPolicy, Assert.Single(limited.Headers.GetValues("Content-Security-Policy")));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var health = await client.SendAsync(HttpMethod.Get, "/health/live");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
    }

    [Fact]
    public async Task The_global_limit_is_in_addition_to_the_sign_in_limit_not_instead_of_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: 2);
        var client = factory.CreateApiClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var response = await client.SignInAsync("nobody@example.test");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
    }

    [Fact]
    public void The_global_limit_default_is_three_hundred_a_minute_and_a_zero_is_refused()
    {
        Assert.Equal(300, new Api.Security.RateLimitingOptions().GlobalPerIpPerMinute);
        var validator = new Api.Security.RateLimitingOptionsValidator();

        Assert.True(validator.Validate(null, new Api.Security.RateLimitingOptions()).Succeeded);
        var refused = validator.Validate(null, new Api.Security.RateLimitingOptions { GlobalPerIpPerMinute = 0 });
        Assert.True(refused.Failed);
        Assert.Contains("RateLimiting:GlobalPerIpPerMinute", refused.FailureMessage, StringComparison.Ordinal);
    }

    // A problem built by hand (the validation and refusal results are) is given the trace id by the one helper.
    [Fact]
    public void An_action_that_returns_a_problem_has_it_given_the_trace_id()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "trace-123" };
        var problem = new ProblemDetails { Status = 400 };

        ProblemResponses.AddTraceId(problem, context);

        Assert.Equal("trace-123", problem.Extensions["traceId"]);
    }
}
