using Npgsql;
using Testcontainers.PostgreSql;

namespace CaseRegister.Tests.Database;

/// <summary>
/// One real PostgreSQL server for the database tests, on the same major version as the Neon project
/// (18). Each test gets its own empty database on it, so tests never see each other's data.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string AppRole = "case_register_app";
    public const string AppRolePassword = "test-only-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine")
        // Every test gets its own database and app instance. Plenty of headroom keeps pool leftovers from ever mattering.
        .WithCommand("-c", "max_connections=500")
        .Build();
    private readonly Lazy<Task> _appRole;

    public PostgresFixture()
    {
        // Lazy<Task> runs the creation once, however many tests ask for it at the same time.
        _appRole = new Lazy<Task>(() => ExecuteAsync(
            _container.GetConnectionString(),
            $"CREATE ROLE {AppRole} LOGIN PASSWORD '{AppRolePassword}'"));
    }

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await _container.DisposeAsync();
    }

    /// <summary>Creates an empty database and returns a connection string for it, as the server's admin user.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await ExecuteAsync(_container.GetConnectionString(), $"CREATE DATABASE {name}");

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = name,

            // Small and short-lived pools: there is one per test database, and they should not linger.
            MaxPoolSize = 8,
            ConnectionPruningInterval = 2,
            ConnectionIdleLifetime = 4,
        }.ConnectionString;
    }

    /// <summary>Creates the application login role once per server, the way the runbook describes (plain SQL).</summary>
    public Task EnsureAppRoleAsync() => _appRole.Value;

    /// <summary>The same database, connected as the least-privilege application role.</summary>
    public static string AsAppRole(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Username = AppRole, Password = AppRolePassword }.ConnectionString;

    /// <summary>Closes every idle connection held for this connection string.</summary>
    public static void ReleaseConnections(string? connectionString)
    {
        // Some tests deliberately start the app with a missing or broken connection string.
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith("Host=localhost;Port=1;", StringComparison.Ordinal))
        {
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? default : (T)value;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresTestGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
