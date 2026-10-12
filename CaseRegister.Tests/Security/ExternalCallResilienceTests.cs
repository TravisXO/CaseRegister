using System.Diagnostics;
using System.Net;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Security;

/// <summary>The calls to Have I Been Pwned and Turnstile, built the way the application builds them, against a server that misbehaves.</summary>
public sealed class ExternalCallResilienceTests
{
    // The real code path for sign-in: a failure of either service must end quickly and in a safe answer, never an exception.
    private const string BreachSuffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8"; // SHA-1 of "password" after the five-character prefix

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, Interlocked.Increment(ref _calls), cancellationToken);
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body = "") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static ServiceProvider Build(ScriptedHandler handler, Action<ExternalCallOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Turnstile:SecretKey"] = "test-secret" }).Build());

        DependencyInjection.AddExternalClients(services);

        // Fast timings, so a test that waits for a timeout does not take seconds. The code path is the same.
        services.Configure<ExternalCallOptions>(options =>
        {
            options.AttemptTimeout = TimeSpan.FromMilliseconds(300);
            options.TotalTimeout = TimeSpan.FromSeconds(2);
            options.MaxRetryAttempts = 2;
            options.RetryDelay = TimeSpan.FromMilliseconds(5);
            configure?.Invoke(options);
        });

        // Only the network is replaced; the resilience handler sits above it as it does in the application.
        services.AddHttpClient<IBreachedPasswordChecker, HaveIBeenPwnedClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>().ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider();
    }

    // --- Have I Been Pwned -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_breach_check_that_fails_twice_and_then_works_is_retried_and_gives_the_real_answer()
    {
        var handler = new ScriptedHandler((_, call, _) => call < 3 ? Reply(HttpStatusCode.ServiceUnavailable) : Reply(HttpStatusCode.OK, $"{BreachSuffix}:12"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IBreachedPasswordChecker>().CheckAsync("password");

        Assert.Equal(BreachCheckResult.Breached, result);
        Assert.Equal(3, handler.Calls); // the first try and two retries
    }

    [Fact]
    public async Task A_breach_check_that_keeps_failing_gives_up_after_the_retries_with_unavailable()
    {
        var handler = new ScriptedHandler((_, _, _) => Reply(HttpStatusCode.InternalServerError));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IBreachedPasswordChecker>().CheckAsync("password");

        Assert.Equal(BreachCheckResult.Unavailable, result);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_network_error_is_retried()
    {
        var handler = new ScriptedHandler((_, call, _) => call == 1 ? throw new HttpRequestException("connection reset") : Reply(HttpStatusCode.OK, "0018A45C4D1DEF81644B54AB7F969B88D65:1"));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IBreachedPasswordChecker>().CheckAsync("password");

        Assert.Equal(BreachCheckResult.NotBreached, result);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_refusal_the_service_means_is_not_retried()
    {
        var handler = new ScriptedHandler((_, _, _) => Reply(HttpStatusCode.BadRequest));
        await using var provider = Build(handler);

        var result = await provider.GetRequiredService<IBreachedPasswordChecker>().CheckAsync("password");

        Assert.Equal(BreachCheckResult.Unavailable, result);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_breach_check_that_hangs_is_cut_off_and_answers_unavailable_within_the_total_timeout()
    {
        var handler = new ScriptedHandler(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var provider = Build(handler);
        var timer = Stopwatch.StartNew();

        var result = await provider.GetRequiredService<IBreachedPasswordChecker>().CheckAsync("password");
        timer.Stop();

        Assert.Equal(BreachCheckResult.Unavailable, result);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
        Assert.True(handler.Calls is >= 2 and <= 3, "each hung try was ended by the attempt timeout, and retried");
    }

    // --- Turnstile ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_verification_that_fails_twice_and_then_works_is_retried_and_passes()
    {
        var handler = new ScriptedHandler((_, call, _) => call < 3 ? Reply(HttpStatusCode.BadGateway) : Reply(HttpStatusCode.OK, "{\"success\":true}"));
        await using var provider = Build(handler);

        var passed = await provider.GetRequiredService<ITurnstileVerifier>().VerifyAsync("token-1", "203.0.113.5");

        Assert.True(passed);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_verification_that_keeps_failing_is_a_no_and_never_an_exception()
    {
        var handler = new ScriptedHandler((_, _, _) => Reply(HttpStatusCode.ServiceUnavailable));
        await using var provider = Build(handler);

        Assert.False(await provider.GetRequiredService<ITurnstileVerifier>().VerifyAsync("token-1", null));
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task A_verification_that_hangs_is_a_no_within_the_total_timeout()
    {
        var handler = new ScriptedHandler(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var provider = Build(handler);
        var timer = Stopwatch.StartNew();

        var passed = await provider.GetRequiredService<ITurnstileVerifier>().VerifyAsync("token-1", null);
        timer.Stop();

        Assert.False(passed);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"took {timer.Elapsed}");
    }

    [Fact]
    public async Task A_caller_who_gives_up_is_not_treated_as_a_failure_of_the_service()
    {
        var handler = new ScriptedHandler(async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var provider = Build(handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetRequiredService<ITurnstileVerifier>().VerifyAsync("token-1", null, cancel.Token));
    }

    // --- The settings -------------------------------------------------------------------------------------------

    [Fact]
    public void The_defaults_are_three_seconds_a_try_eight_seconds_in_all_two_retries_and_a_short_first_wait()
    {
        var options = new ExternalCallOptions();

        Assert.Equal(TimeSpan.FromSeconds(3), options.AttemptTimeout);
        Assert.Equal(TimeSpan.FromSeconds(8), options.TotalTimeout);
        Assert.Equal(2, options.MaxRetryAttempts);
        Assert.Equal(TimeSpan.FromMilliseconds(300), options.RetryDelay);
        Assert.True(ExternalCalls.ClientTimeout > options.TotalTimeout, "the HTTP client's own timeout must not end a call before the pipeline does");
    }

    [Fact]
    public void The_real_clients_get_the_standard_resilience_handler_with_those_settings()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Turnstile:SecretKey"] = "x" }).Build());
        DependencyInjection.AddExternalClients(services);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>>();
        foreach (var name in new[] { "IBreachedPasswordChecker-standard", "ITurnstileVerifier-standard" })
        {
            var settings = options.Get(name);

            Assert.Equal(TimeSpan.FromSeconds(3), settings.AttemptTimeout.Timeout);
            Assert.Equal(TimeSpan.FromSeconds(8), settings.TotalRequestTimeout.Timeout);
            Assert.Equal(2, settings.Retry.MaxRetryAttempts);
            Assert.True(settings.Retry.UseJitter);
        }
    }

    [Theory]
    [InlineData(0, 8, 2, 300, "ExternalCalls:AttemptTimeout")]
    [InlineData(3, 3, 2, 300, "ExternalCalls:TotalTimeout")]
    [InlineData(3, 2, 2, 300, "ExternalCalls:TotalTimeout")]
    [InlineData(3, 8, -1, 300, "ExternalCalls:MaxRetryAttempts")]
    [InlineData(3, 8, 6, 300, "ExternalCalls:MaxRetryAttempts")]
    [InlineData(3, 8, 2, 0, "ExternalCalls:RetryDelay")]
    public void Settings_that_make_no_sense_are_refused_naming_the_setting(int attemptSeconds, int totalSeconds, int retries, int delayMilliseconds, string expectedSetting)
    {
        var options = new ExternalCallOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(attemptSeconds),
            TotalTimeout = TimeSpan.FromSeconds(totalSeconds),
            MaxRetryAttempts = retries,
            RetryDelay = TimeSpan.FromMilliseconds(delayMilliseconds),
        };

        var result = new ExternalCallOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(expectedSetting, result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_settings_are_accepted()
    {
        Assert.True(new ExternalCallOptionsValidator().Validate(null, new ExternalCallOptions()).Succeeded);
    }
}
