using System.Net;
using System.Text.Json;
using CaseRegister.Api.Hardening;
using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaseRegister.Tests.Hardening;

/// <summary>The whole application started the way it runs on Render, and the pieces that are different in Development.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EnvironmentTests(PostgresFixture postgres)
{
    private const string StrictPolicy = "default-src 'none'; frame-ancestors 'none'";
    private static readonly string[] _apiDescriptionAndReferencePaths = ["/openapi/v1.json", "/openapi", "/scalar", "/scalar/v1", "/scalar/scalar.js", "/swagger", "/swagger/v1/swagger.json"];

    private Task<ApiFactory> ProductionAsync(Action<Dictionary<string, string?>>? adjust = null) =>
        ApiFactory.CreateAsync(postgres, adjust, environmentName: "Production");

    // --- Production --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_application_really_starts_in_production()
    {
        await using var factory = await ProductionAsync();

        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();

        Assert.Equal("Production", environment.EnvironmentName);
        Assert.False(environment.IsDevelopment());
    }

    [Fact]
    public async Task Production_sends_strict_transport_security_and_the_strict_content_security_policy_on_every_response()
    {
        await using var factory = await ProductionAsync();
        using var client = factory.Https();

        foreach (var path in new[] { "/health/live", "/probe/ip", "/probe/members-only", "/does/not/exist", "/probe/throw" })
        {
            using var response = await client.SendAsync(HttpMethod.Get, path);

            Assert.Equal("max-age=31536000; includeSubDomains", Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
            Assert.Equal(StrictPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
            Assert.Equal("no-store", Assert.Single(response.Headers.GetValues("Cache-Control")));
            Assert.False(response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task Production_has_no_api_description_and_no_reference_page_for_anyone()
    {
        await using var factory = await ProductionAsync();
        var user = await factory.CreateUserAsync("prod.reader@example.test");
        var signedIn = factory.CreateApiClient();
        await signedIn.SignInWithTwoFactorAsync(factory, user);
        using var anonymous = factory.Https();

        foreach (var path in _apiDescriptionAndReferencePaths)
        {
            using var asStranger = await anonymous.SendAsync(HttpMethod.Get, path);
            using var asMember = await signedIn.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, asStranger.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, asMember.StatusCode);
            foreach (var text in new[] { await asStranger.Content.ReadAsStringAsync(), await asMember.Content.ReadAsStringAsync() })
            {
                Assert.DoesNotContain("openapi", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("scalar", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("swagger", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Production_starts_with_no_demo_data_and_the_demo_command_is_refused_there()
    {
        await using var factory = await ProductionAsync();
        using var client = factory.Https();
        using var live = await client.SendAsync(HttpMethod.Get, "/health/live");
        var terminal = new Auth.ScriptedTerminal([], []);

        var exit = await CaseRegister.Api.Cli.DemoCommands.RunAsync([CaseRegister.Api.Cli.DemoCommands.Name], factory.Services, terminal);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(CaseRegister.Api.Cli.DemoCommands.Refused, exit);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
    }

    [Fact]
    public async Task Production_errors_carry_no_stack_trace_exception_message_sql_or_input()
    {
        await using var factory = await ProductionAsync();
        using var client = factory.Https();

        using var failure = await client.SendAsync(HttpMethod.Get, "/probe/throw");
        var apiClient = factory.CreateApiClient();
        using var badBody = await apiClient.SendContentAsync(HttpMethod.Post, "/probe/json", HardeningHelpers.Json("{\"name\":\"visible-input-4471\",\"count\":\"nope\",\"extra\":1}"));

        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        foreach (var response in new[] { failure, badBody })
        {
            var text = await response.Content.ReadAsStringAsync();
            var problem = JsonDocument.Parse(text).RootElement;
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
            Assert.False(problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String);
            foreach (var forbidden in new[] { "SELECT", "123456/78/9", "InvalidOperationException", "   at ", "StackTrace", "Exception", "visible-input-4471", "ProbeBody", "System.", "Developer" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Production_redirects_plain_http_to_https_with_a_308_that_keeps_the_method()
    {
        await using var factory = await ProductionAsync();
        using var plain = factory.Http();

        using var get = await plain.SendAsync(HttpMethod.Get, "/probe/ip");
        using var post = await plain.SendAsync(HttpMethod.Post, "/probe", HardeningHelpers.Json("{}"));

        Assert.Equal(HttpStatusCode.PermanentRedirect, get.StatusCode);
        Assert.Equal("https://localhost/probe/ip", get.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.PermanentRedirect, post.StatusCode);
        Assert.Equal("https://localhost/probe", post.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Behind_the_platforms_proxy_a_request_that_was_https_outside_is_served_not_redirected()
    {
        await using var factory = await ProductionAsync();
        using var behindProxy = factory.Http(); // the proxy talks plain http to the app

        using var viaHttps = await behindProxy.SendAsync(HttpMethod.Get, "/probe/ip", content: null, ("X-Forwarded-Proto", "https"));
        using var viaHttp = await behindProxy.SendAsync(HttpMethod.Get, "/probe/ip", content: null, ("X-Forwarded-Proto", "http"));

        Assert.Equal(HttpStatusCode.OK, viaHttps.StatusCode);
        Assert.Equal("max-age=31536000; includeSubDomains", Assert.Single(viaHttps.Headers.GetValues("Strict-Transport-Security")));
        Assert.Equal(HttpStatusCode.PermanentRedirect, viaHttp.StatusCode);
    }

    [Fact]
    public async Task Following_the_redirect_ends_after_one_step_and_there_is_no_loop()
    {
        await using var factory = await ProductionAsync();
        using var plain = factory.Http();
        using var secure = factory.Https();

        using var first = await plain.SendAsync(HttpMethod.Get, "/probe/ip");
        using var second = await secure.SendAsync(HttpMethod.Get, first.Headers.Location!.PathAndQuery);

        Assert.Equal(HttpStatusCode.PermanentRedirect, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Null(second.Headers.Location);

        // A proxy that sets the header on the very same request is never redirected, however many times it asks.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var again = await plain.SendAsync(HttpMethod.Get, "/probe/ip", content: null, ("X-Forwarded-Proto", "https"));
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }
    }

    [Fact]
    public async Task Production_refuses_to_start_with_the_local_file_anchor_store_and_never_shows_the_path()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Audit:AnchorFilePath"] = "/private-dir-4411/anchors.jsonl", environmentName: "Production");

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("Audit:AnchorFilePath", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-dir-4411", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Testing", true)]
    [InlineData("Staging", true)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    public void The_local_file_anchor_store_is_allowed_only_in_development(string environment, bool refused)
    {
        var options = new AuditOptions { CurrentKeyId = ApiFactory.TestKeyId, Keys = { [ApiFactory.TestKeyId] = ApiFactory.TestKeyBase64 }, AnchorFilePath = "App_Data/audit-anchors.jsonl" };
        var validator = new AuditOptionsValidator(new FakeHostEnvironment(environment));

        var result = validator.Validate(null, options);

        Assert.Equal(refused, result.Failed);
        if (refused)
        {
            Assert.Contains("Audit:AnchorFilePath", result.FailureMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("App_Data", result.FailureMessage, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public void No_anchor_store_at_all_is_not_a_startup_error_outside_production(string environment)
    {
        var options = new AuditOptions { CurrentKeyId = ApiFactory.TestKeyId, Keys = { [ApiFactory.TestKeyId] = ApiFactory.TestKeyBase64 } };

        Assert.True(new AuditOptionsValidator(new FakeHostEnvironment(environment)).Validate(null, options).Succeeded);
    }

    [Fact]
    public void Production_refuses_to_start_with_no_anchor_store_and_names_the_first_missing_setting()
    {
        var options = new AuditOptions { CurrentKeyId = ApiFactory.TestKeyId, Keys = { [ApiFactory.TestKeyId] = ApiFactory.TestKeyBase64 } };

        var result = new AuditOptionsValidator(new FakeHostEnvironment("Production")).Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("Audit:R2:Endpoint", result.FailureMessage, StringComparison.Ordinal);
    }

    // --- Development only ----------------------------------------------------------------------------------------

    /// <summary>A small host that has the application's hardening and reference page, in the environment asked for, and nothing else.</summary>
    private static async Task<WebApplication> StartMiniHostAsync(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment, ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseTestServer();

        // Production will not start without the proxy secret, and the clients below send it, as the real proxy does.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Proxy:Secret"] = ApiFactory.ProxyTestSecret });
        builder.Services.AddSingleton(new LogOutputOverride(new CapturingLogEventSink()));
        builder.AddApiHardening();
        builder.Services.AddApiDescription();

        var app = builder.Build();
        app.UseApiHardening();
        app.MapGet("/boom", (Func<IResult>)(() => throw new InvalidOperationException("the reason a developer wants to see"))).AllowAnonymous();
        app.MapOperationalEndpoints();
        app.MapApiReference();
        await app.StartAsync();

        return app;
    }

    private static HttpClient SecureClient(WebApplication app)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add(ApiFactory.ProxyHeader, ApiFactory.ProxyTestSecret);

        return client;
    }

    [Fact]
    public async Task Development_serves_the_reference_page_with_its_own_looser_policy_that_still_forbids_framing()
    {
        await using var app = await StartMiniHostAsync("Development");
        using var client = SecureClient(app);

        using var response = await client.SendAsync(HttpMethod.Get, "/scalar/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.ApiReferenceContentSecurityPolicy, policy);
        Assert.NotEqual(StrictPolicy, policy);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_serves_the_api_description_and_the_looser_policy_applies_to_the_reference_page_only()
    {
        await using var app = await StartMiniHostAsync("Development");
        using var client = SecureClient(app);

        using var description = await client.SendAsync(HttpMethod.Get, "/openapi/v1.json");
        using var health = await client.SendAsync(HttpMethod.Get, "/health/live");

        Assert.Equal(HttpStatusCode.OK, description.StatusCode);
        Assert.Contains("\"openapi\"", await description.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(StrictPolicy, Assert.Single(description.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(StrictPolicy, Assert.Single(health.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Development_sends_no_strict_transport_security_so_localhost_is_not_pinned_to_https()
    {
        await using var app = await StartMiniHostAsync("Development");
        using var client = SecureClient(app);

        using var response = await client.SendAsync(HttpMethod.Get, "/health/live");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Development_shows_the_reason_for_a_failure_to_the_developer_and_production_does_not()
    {
        await using var development = await StartMiniHostAsync("Development");
        await using var production = await StartMiniHostAsync("Production");
        using var developmentClient = SecureClient(development);
        using var productionClient = SecureClient(production);

        using var inDevelopment = await developmentClient.SendAsync(HttpMethod.Get, "/boom");
        using var inProduction = await productionClient.SendAsync(HttpMethod.Get, "/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, inDevelopment.StatusCode);
        Assert.Contains("the reason a developer wants to see", await inDevelopment.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, inProduction.StatusCode);
        Assert.DoesNotContain("the reason a developer wants to see", await inProduction.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task Outside_development_the_reference_page_and_the_description_are_not_there_and_the_strict_policy_applies_even_to_their_addresses(string environment)
    {
        await using var app = await StartMiniHostAsync(environment);
        using var client = SecureClient(app);

        foreach (var path in new[] { "/scalar/v1", "/scalar", "/openapi/v1.json" })
        {
            using var response = await client.SendAsync(HttpMethod.Get, path);

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(StrictPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        }
    }
}
