using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// The one place the Npgsql provider is configured, shared by the application, the migrator and
/// the design-time factory so they cannot drift apart.
/// </summary>
internal static class AppDbContextOptions
{
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(5);

    // Explicit rather than the provider default, so a hung query cannot hold a request open forever.
    private const int CommandTimeoutSeconds = 30;

    public static void Configure(DbContextOptionsBuilder builder, string connectionString)
    {
        builder
            .UseNpgsql(connectionString, npgsql =>
            {
                // Neon computes suspend when idle, so the first connection after a pause can fail
                // while the compute wakes up. Retry transient failures instead of surfacing them.
                npgsql.EnableRetryOnFailure(MaxRetryCount, _maxRetryDelay, errorCodesToAdd: null);
                npgsql.CommandTimeout(CommandTimeoutSeconds);
                npgsql.MigrationsHistoryTable("__ef_migrations_history");
            })
            .UseSnakeCaseNamingConvention()

            // Stated, though off by default: parameter values (which can be NRCs or tokens) must never reach a log, here or anywhere.
            .EnableSensitiveDataLogging(false)
            .EnableDetailedErrors(false);
    }
}
