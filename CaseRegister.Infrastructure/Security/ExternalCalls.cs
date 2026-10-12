using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Security;

/// <summary>How the calls out to Have I Been Pwned and Cloudflare Turnstile behave when the other side is slow or failing.</summary>
public sealed class ExternalCallOptions
{
    public const string SectionName = "ExternalCalls";

    /// <summary>One try. A sign-in waits for this call, so it cannot be long.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>All tries together, including the waits between them.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Extra tries after a timeout, a network error or a 5xx, 408 or 429 answer.</summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>The first wait before a retry (it grows, with jitter).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(300);
}

internal sealed class ExternalCallOptionsValidator : IValidateOptions<ExternalCallOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalCallOptions options)
    {
        var problems = new List<string>();
        if (options.AttemptTimeout <= TimeSpan.Zero)
        {
            problems.Add($"{ExternalCallOptions.SectionName}:AttemptTimeout must be longer than zero.");
        }

        if (options.TotalTimeout <= options.AttemptTimeout)
        {
            problems.Add($"{ExternalCallOptions.SectionName}:TotalTimeout must be longer than {ExternalCallOptions.SectionName}:AttemptTimeout.");
        }

        if (options.MaxRetryAttempts is < 0 or > 5)
        {
            problems.Add($"{ExternalCallOptions.SectionName}:MaxRetryAttempts must be from 0 to 5.");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            problems.Add($"{ExternalCallOptions.SectionName}:RetryDelay must be longer than zero.");
        }

        return problems.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}

internal static class ExternalCalls
{
    /// <summary>The HTTP client's own limit: past every timeout below, so the resilience pipeline is what ends a call.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Timeouts, retries with backoff, and a circuit breaker, all from <see cref="ExternalCallOptions"/>.</summary>
    public static IHttpStandardResiliencePipelineBuilder AddExternalCallResilience(this IHttpClientBuilder client) =>
        client.AddStandardResilienceHandler().Configure((resilience, provider) =>
        {
            var options = provider.GetRequiredService<IOptions<ExternalCallOptions>>().Value;

            resilience.AttemptTimeout.Timeout = options.AttemptTimeout;
            resilience.TotalRequestTimeout.Timeout = options.TotalTimeout;
            resilience.Retry.MaxRetryAttempts = options.MaxRetryAttempts;
            resilience.Retry.Delay = options.RetryDelay;
            resilience.Retry.UseJitter = true;

            // The breaker's window has to be at least twice an attempt, or the pipeline refuses to build.
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(30).Ticks, options.AttemptTimeout.Ticks * 2));
        });
}
