using System.Net;
using System.Text.Json;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Tests.Hardening;

/// <summary>One line per request, saying what happened and nothing that identifies a person by their email or hides a secret.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class RequestLogTests(PostgresFixture postgres)
{
    private const string Email = "ada.logs@example.test";

    private static List<JsonElement> RequestLines(ApiFactory factory) =>
        [.. factory.LogOutput.Lines.Select(line => JsonDocument.Parse(line).RootElement).Where(line => line.TryGetProperty("Route", out _))];

    private static JsonElement LineFor(ApiFactory factory, string route) =>
        Assert.Single(RequestLines(factory), line => line.GetProperty("Route").GetString() == route);

    [Fact]
    public async Task A_request_logs_its_method_route_template_status_duration_correlation_id_and_user()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/probe/ip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = LineFor(factory, "probe/ip");
        Assert.Equal("GET", line.GetProperty("Method").GetString());
        Assert.Equal(200, line.GetProperty("Status").GetInt32());
        Assert.True(line.GetProperty("ElapsedMs").GetDouble() >= 0);
        Assert.False(string.IsNullOrEmpty(line.GetProperty("CorrelationId").GetString()));
        Assert.Equal("anonymous", line.GetProperty("UserId").GetString());
        Assert.False(line.TryGetProperty("@l", out _)); // Information is the default level, so it is not written
    }

    [Fact]
    public async Task There_is_exactly_one_line_for_each_request()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Https();

        for (var count = 0; count < 3; count++)
        {
            using var response = await client.SendAsync(HttpMethod.Get, "/probe/ip");
            Assert.Equal(count + 1, RequestLines(factory).Count);
        }
    }

    [Fact]
    public async Task A_signed_in_request_logs_the_account_id_and_never_the_email_address_or_the_password()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        using var signedIn = await client.SignInWithTwoFactorAsync(factory, user);
        using var session = await client.GetAsync("/api/auth/session");
        using var wrong = await factory.CreateApiClient().SignInAsync(Email, "not the right password at all");

        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var line = LineFor(factory, "api/auth/session");
        Assert.Equal(user.Id.ToString(), line.GetProperty("UserId").GetString());

        // Nothing written anywhere carries the address, either password, or the code.
        foreach (var written in factory.LogOutput.Lines)
        {
            Assert.DoesNotContain(Email, written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ApiFactory.Password, written, StringComparison.Ordinal);
            Assert.DoesNotContain("not the right password", written, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_route_is_the_template_never_the_address_that_was_asked_for()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, user);
        var someSessionId = Guid.NewGuid();

        using var response = await client.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{someSessionId}");

        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        LineFor(factory, "api/auth/sessions/{id:guid}");
        var leaked = factory.LogOutput.Lines.Where(written => written.Contains(someSessionId.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(leaked.Count == 0, string.Join(Environment.NewLine, leaked));
    }

    [Fact]
    public async Task An_address_that_matches_nothing_is_logged_as_no_route_and_its_path_is_not_logged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/some/private-looking-path-4417");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var line = Assert.Single(RequestLines(factory), written => written.GetProperty("Status").GetInt32() == 401);
        Assert.Equal("{*path:nonfile}", line.GetProperty("Route").GetString()); // the catch-all's own template
        Assert.DoesNotContain(factory.LogOutput.Lines, written => written.Contains("private-looking-path-4417", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_is_logged_at_error_level_with_the_500_it_became()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var client = factory.Https();

        using var response = await client.SendAsync(HttpMethod.Get, "/probe/throw");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var line = LineFor(factory, "probe/throw");
        Assert.Equal(500, line.GetProperty("Status").GetInt32());
        Assert.Equal("Error", line.GetProperty("@l").GetString());
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "A test of what the logger writes, with messages that are not meant to be reused.")]
    public async Task Secrets_logged_by_the_application_itself_reach_the_console_only_in_redacted_form()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        _ = factory.CreateApiClient(); // starts the host
        using var scope = factory.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SomeService");

        logger.LogWarning("Sign-in used password={Password} and token={Token} and code {Code}", "pw-in-the-log-1", "tok-in-the-log-2", "123456");
        logger.LogWarning("Cookie: __Host-cr=CfDJ8LoggedCookie; applicant {Nrc}; Host=db.example;Password=conn-secret-3", "123456/78/9");
        logger.LogError(new InvalidOperationException("failed with connection Host=db.example;Username=u;Password=exception-secret-4"), "It failed");

        var written = string.Join('\n', factory.LogOutput.Lines);
        foreach (var secret in new[] { "pw-in-the-log-1", "tok-in-the-log-2", "123456", "CfDJ8LoggedCookie", "conn-secret-3", "exception-secret-4", "db.example" })
        {
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
        }

        // The application's own logger (before redaction) did see them, so it is the redaction step that removed them.
        Assert.Contains(factory.Logs.Entries, entry => entry.Message.Contains("pw-in-the-log-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Serilog_is_the_only_logger_so_nothing_reaches_the_console_unredacted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        _ = factory.CreateApiClient(); // starts the host

        var providers = factory.Services.GetServices<ILoggerProvider>().Select(provider => provider.GetType().Name).ToList();

        Assert.DoesNotContain(providers, name => name.Contains("Console", StringComparison.Ordinal) || name.Contains("Debug", StringComparison.Ordinal) || name.Contains("EventSource", StringComparison.Ordinal));
        Assert.Contains("Serilog", factory.Services.GetRequiredService<ILoggerFactory>().GetType().Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sensitive_data_logging_is_off_so_parameter_values_never_reach_a_log()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>();

        var core = options.FindExtension<CoreOptionsExtension>();

        Assert.NotNull(core);
        Assert.False(core.IsSensitiveDataLoggingEnabled);
        Assert.False(core.DetailedErrorsEnabled);
    }

    [Fact]
    public async Task A_query_that_runs_leaves_no_sql_text_in_the_console_output()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();

        using var response = await client.SignInAsync(user.Email!);

        Assert.DoesNotContain(factory.LogOutput.Lines, written => written.Contains("SELECT", StringComparison.Ordinal) || written.Contains("@p0", StringComparison.Ordinal));
    }
}
