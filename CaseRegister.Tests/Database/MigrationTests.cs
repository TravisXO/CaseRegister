using System.Reflection;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CaseRegister.Tests.Database;

[Collection(PostgresTestGroup.Name)]
public class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task All_migrations_apply_cleanly_to_an_empty_database()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        await DatabaseMigrator.MigrateAsync(connectionString);

        var applied = await AppliedMigrationsAsync(connectionString);
        Assert.Equal(MigrationsInAssembly(), applied);
        Assert.NotEmpty(applied);
    }

    [Fact]
    public async Task Migrating_twice_changes_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        await DatabaseMigrator.MigrateAsync(connectionString);
        await DatabaseMigrator.MigrateAsync(connectionString);

        Assert.Equal(MigrationsInAssembly(), await AppliedMigrationsAsync(connectionString));
    }

    [Fact]
    public async Task The_pg_trgm_extension_is_installed_after_migrating()
    {
        var connectionString = await postgres.CreateDatabaseAsync();

        await DatabaseMigrator.MigrateAsync(connectionString);

        var installed = await PostgresFixture.ScalarAsync<long>(
            connectionString, "SELECT count(*) FROM pg_extension WHERE extname = 'pg_trgm'");
        Assert.Equal(1, installed);

        // Installed and actually usable for fuzzy name matching.
        var similarity = await PostgresFixture.ScalarAsync<float>(
            connectionString, "SELECT similarity('Mwansa', 'Mwanza')");
        Assert.InRange(similarity, 0.1f, 1f);
    }

    [Fact]
    public async Task The_model_has_no_changes_missing_from_the_migrations()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, connectionString);

        await using var context = new AppDbContext(builder.Options);

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The model has changed since the last migration. Add a migration with 'dotnet ef migrations add'.");
    }

    private static List<string> MigrationsInAssembly() =>
        typeof(AppDbContext).Assembly.GetTypes()
            .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async Task<List<string>> AppliedMigrationsAsync(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, connectionString);

        await using var context = new AppDbContext(builder.Options);

        return (await context.Database.GetAppliedMigrationsAsync()).ToList();
    }
}
