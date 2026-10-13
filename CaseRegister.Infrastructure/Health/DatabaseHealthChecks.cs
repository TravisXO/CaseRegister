using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CaseRegister.Infrastructure.Health;

/// <summary>Whether the database answers a trivial query. The reason for a failure goes to the logs through the framework, never to the caller.</summary>
internal sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("The database cannot be reached.");
    }
}

/// <summary>
/// Remembers the answer of another check for a few seconds, so a flood of readiness calls (or a monitor that asks often)
/// cannot keep the database awake or busy.
/// </summary>
internal sealed class CachedHealthCheck(IHealthCheck inner, TimeProvider time, TimeSpan lifetime) : IHealthCheck, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult _last;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (now - _checkedAt < lifetime)
            {
                return _last;
            }

            _last = await inner.CheckHealthAsync(context, cancellationToken);
            _checkedAt = time.GetUtcNow();

            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

public static class HealthChecksSetup
{
    public const string ReadyTag = "ready";
    public const string DatabaseCheckName = "database";

    /// <summary>How long a readiness answer is reused.</summary>
    public static readonly TimeSpan ReadyCacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>The checks behind /health/ready. Nothing is registered for /health/live: it has no dependencies.</summary>
    public static IServiceCollection AddReadinessChecks(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseHealthCheck>();
        services.AddHealthChecks().Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
            DatabaseCheckName,
            provider => new CachedHealthCheck(provider.GetRequiredService<DatabaseHealthCheck>(), provider.GetRequiredService<TimeProvider>(), ReadyCacheLifetime),
            failureStatus: HealthStatus.Unhealthy,
            tags: [ReadyTag]));

        return services;
    }
}
