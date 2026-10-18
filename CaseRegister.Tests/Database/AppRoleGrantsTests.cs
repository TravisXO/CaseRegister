using System.Text.RegularExpressions;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Architecture;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>Proves the runtime role really is least-privilege: data access only, no DDL.</summary>
[Collection(PostgresTestGroup.Name)]
public partial class AppRoleGrantsTests(PostgresFixture postgres)
{
    private static readonly string _scriptPath =
        Path.Combine(SolutionGraph.SolutionRoot, "database", "app-role-grants.sql");

    [Fact]
    public void The_grant_script_contains_no_credentials()
    {
        var code = string.Join('\n', File.ReadAllLines(_scriptPath).Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        Assert.DoesNotMatch(CredentialLiteral(), code);
    }

    [Fact]
    public async Task The_unused_lockout_table_and_the_duplicate_enrolment_column_are_gone_and_the_other_used_at_columns_stay()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();

        Assert.Equal(0, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM information_schema.tables WHERE table_name = 'lockout_events'"));
        Assert.Equal(0, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'enrolment_codes' AND column_name = 'completed_at'"));
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'enrolment_codes' AND column_name = 'used_at'"));
    }

    [Fact]
    public async Task The_app_role_can_read_and_write_data_on_existing_and_future_tables()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        await PostgresFixture.ExecuteAsync(owner, "CREATE TABLE before_grants_rerun (id int PRIMARY KEY, note text)");
        await RunGrantScriptAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, "CREATE TABLE created_later (id int PRIMARY KEY, note text)");

        var app = PostgresFixture.AsAppRole(owner);
        foreach (var table in new[] { "before_grants_rerun", "created_later" })
        {
            await PostgresFixture.ExecuteAsync(app, $"INSERT INTO {table} VALUES (1, 'a')");
            await PostgresFixture.ExecuteAsync(app, $"UPDATE {table} SET note = 'b'");
            Assert.Equal("b", await PostgresFixture.ScalarAsync<string>(app, $"SELECT note FROM {table}"));
            await PostgresFixture.ExecuteAsync(app, $"DELETE FROM {table}");
        }
    }

    [Theory]
    [InlineData("CREATE TABLE sneaky (id int)")]
    [InlineData("CREATE SCHEMA sneaky")]
    [InlineData("CREATE EXTENSION IF NOT EXISTS hstore")]
    [InlineData("CREATE ROLE sneaky")]
    [InlineData("CREATE DATABASE sneaky")]
    [InlineData("ALTER TABLE seeded ADD COLUMN sneaky int")]
    [InlineData("DROP TABLE seeded")]
    [InlineData("TRUNCATE seeded")]
    public async Task The_app_role_cannot_change_the_schema_or_the_server(string statement)
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        await PostgresFixture.ExecuteAsync(owner, "CREATE TABLE seeded (id int)");
        await RunGrantScriptAsync(owner);

        var denied = await Assert.ThrowsAsync<PostgresException>(
            () => PostgresFixture.ExecuteAsync(PostgresFixture.AsAppRole(owner), statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private const string InsertAuditRow =
        "INSERT INTO audit_log (id, sequence, occurred_at, actor_type, action, previous_hash, entry_hash, key_id, created_at, updated_at) "
        + "VALUES (gen_random_uuid(), 1, now(), 'system', 'Probe', repeat('0', 64), repeat('a', 64), 'k1', now(), now())";

    [Fact]
    public async Task The_app_role_can_add_and_read_audit_rows()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(app, InsertAuditRow);

        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(app, "SELECT count(*) FROM audit_log"));
    }

    [Theory]
    [InlineData("UPDATE audit_log SET detail = 'tampered'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    public async Task The_audit_log_is_append_only_for_the_app_role(string statement)
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        var app = PostgresFixture.AsAppRole(owner);
        await PostgresFixture.ExecuteAsync(app, InsertAuditRow);

        var denied = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(app, "SELECT count(*) FROM audit_log"));
    }

    [Theory]
    [InlineData("users")]
    [InlineData("user_authenticators")]
    [InlineData("recovery_codes")]
    public async Task Nothing_that_must_be_kept_can_be_deleted_by_the_app_role(string table)
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        var app = PostgresFixture.AsAppRole(owner);

        var delete = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, $"DELETE FROM {table} WHERE false"));
        var truncate = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, $"TRUNCATE {table} CASCADE"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, truncate.SqlState);

        // Reading and writing is still allowed: the application needs that.
        await PostgresFixture.ExecuteAsync(app, $"SELECT count(*) FROM {table}");
    }

    [Fact]
    public async Task The_app_role_can_delete_sessions_because_ending_one_removes_its_row()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(app, "DELETE FROM user_sessions WHERE false");
    }

    [Fact]
    public async Task The_app_role_cannot_touch_the_migration_history()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();

        var denied = await Assert.ThrowsAsync<PostgresException>(
            () => PostgresFixture.ScalarAsync<long>(PostgresFixture.AsAppRole(owner), "SELECT count(*) FROM __ef_migrations_history"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task The_app_role_has_no_elevated_attributes_or_memberships()
    {
        var owner = await MigratedDatabaseWithGrantsAsync();

        var elevated = await PostgresFixture.ScalarAsync<long>(
            owner,
            $"""
            SELECT count(*) FROM pg_roles
            WHERE rolname = '{PostgresFixture.AppRole}'
              AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR rolreplication)
            """);
        var memberships = await PostgresFixture.ScalarAsync<long>(
            owner,
            $"SELECT count(*) FROM pg_auth_members WHERE member = '{PostgresFixture.AppRole}'::regrole");

        Assert.Equal(0, elevated);
        Assert.Equal(0, memberships);
    }

    private async Task<string> MigratedDatabaseWithGrantsAsync()
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);
        await RunGrantScriptAsync(owner);

        return owner;
    }

    // Run twice on purpose: the script has to be safe to re-run.
    private static async Task RunGrantScriptAsync(string ownerConnectionString)
    {
        var script = await File.ReadAllTextAsync(_scriptPath);
        await PostgresFixture.ExecuteAsync(ownerConnectionString, script);
        await PostgresFixture.ExecuteAsync(ownerConnectionString, script);
    }

    // A password literal ("PASSWORD 'secret'") or a connection URL with embedded credentials.
    [GeneratedRegex(@"password\s+'|postgres(ql)?://\S+:\S+@", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLiteral();
}
