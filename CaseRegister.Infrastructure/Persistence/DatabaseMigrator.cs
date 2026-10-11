using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// Applies migrations over the direct (owner) connection. The runtime connection belongs to a role
/// that has no DDL rights, so it can never be used for this.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(string migrationsConnectionString, CancellationToken cancellationToken = default)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, migrationsConnectionString);

        await using var context = new AppDbContext(builder.Options);
        await context.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>True if the database is behind the code. Needs the owner connection: the runtime role cannot read the migration history.</summary>
    public static async Task<bool> HasPendingMigrationsAsync(string migrationsConnectionString, CancellationToken cancellationToken = default)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, migrationsConnectionString);

        await using var context = new AppDbContext(builder.Options);

        return (await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
    }

    public static Task MigrateDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var connectionString = services.GetRequiredService<IOptions<DatabaseOptions>>().Value.Migrations;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{DatabaseOptions.MigrationsKey} is not configured. Set it with 'dotnet user-secrets' locally "
                + "or the ConnectionStrings__Migrations environment variable.");
        }

        return MigrateAsync(connectionString, cancellationToken);
    }
}
