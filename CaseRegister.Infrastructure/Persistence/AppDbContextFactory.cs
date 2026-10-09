using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// Used by the EF Core tools (dotnet ef). Reads ConnectionStrings:Migrations from user-secrets locally
/// or environment variables elsewhere, so migrations run over the direct, non-pooled connection.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<AppDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration[DatabaseOptions.MigrationsKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{DatabaseOptions.MigrationsKey} is not configured. Set it with 'dotnet user-secrets' "
                + "or the ConnectionStrings__Migrations environment variable.");
        }

        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, connectionString);

        return new AppDbContext(builder.Options);
    }
}
