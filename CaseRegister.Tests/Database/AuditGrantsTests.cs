using CaseRegister.Infrastructure.Persistence;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>The database itself refuses to change, remove or empty the audit log, whoever asks.</summary>
[Collection(PostgresTestGroup.Name)]
public class AuditGrantsTests(PostgresFixture postgres)
{
    private static string InsertAudit(int sequence) =>
        "INSERT INTO audit_log (id, sequence, occurred_at, actor_type, action, previous_hash, entry_hash, key_id, created_at, updated_at) "
        + $"VALUES (gen_random_uuid(), {sequence}, now(), 'system', 'Probe', repeat('0', 64), repeat('a', 64), 'k1', now(), now())";

    private async Task<string> OwnerAsync(bool withGrants = true)
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);

        if (withGrants)
        {
            var script = await File.ReadAllTextAsync(Path.Combine(Architecture.SolutionGraph.SolutionRoot, "database", "app-role-grants.sql"));
            await PostgresFixture.ExecuteAsync(owner, script);
        }

        return owner;
    }

    [Theory]
    [InlineData("UPDATE audit_log SET detail = 'tampered'")]
    [InlineData("DELETE FROM audit_log")]
    [InlineData("TRUNCATE audit_log")]
    public async Task Even_the_database_owner_cannot_change_remove_or_empty_audit_rows(string statement)
    {
        var owner = await OwnerAsync();
        await PostgresFixture.ExecuteAsync(owner, InsertAudit(1));

        var refused = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM audit_log"));
    }

    [Theory]
    [InlineData("UPDATE audit_log_legacy SET detail = 'tampered'")]
    [InlineData("DELETE FROM audit_log_legacy")]
    [InlineData("TRUNCATE audit_log_legacy")]
    public async Task The_old_temporary_table_is_protected_in_the_same_way(string statement)
    {
        var owner = await OwnerAsync();
        await PostgresFixture.ExecuteAsync(
            owner,
            "INSERT INTO audit_log_legacy (id, action, created_at, updated_at) VALUES (gen_random_uuid(), 'Probe', now(), now())");

        var refused = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        Assert.Contains("append-only", refused.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_triggers_exist_for_updates_deletes_and_truncates_on_both_tables()
    {
        var owner = await OwnerAsync();

        var triggers = await PostgresFixture.ScalarAsync<long>(
            owner,
            """
            SELECT count(*) FROM pg_trigger
            WHERE NOT tgisinternal AND tgrelid IN ('audit_log'::regclass, 'audit_log_legacy'::regclass)
              AND tgfoid = 'audit_log_refuse_changes'::regproc
            """);

        Assert.Equal(4, triggers);
    }

    [Theory]
    [InlineData("audit_log", "UPDATE")]
    [InlineData("audit_log", "DELETE")]
    [InlineData("audit_log", "TRUNCATE")]
    [InlineData("audit_log_legacy", "INSERT")]
    [InlineData("audit_log_legacy", "UPDATE")]
    [InlineData("audit_log_legacy", "DELETE")]
    [InlineData("audit_log_legacy", "TRUNCATE")]
    [InlineData("audit_chain_head", "INSERT")]
    [InlineData("audit_chain_head", "DELETE")]
    [InlineData("audit_chain_head", "TRUNCATE")]
    public async Task The_app_role_does_not_hold_these_privileges(string table, string privilege)
    {
        var owner = await OwnerAsync();

        var held = await PostgresFixture.ScalarAsync<bool>(owner, $"SELECT has_table_privilege('{PostgresFixture.AppRole}', '{table}', '{privilege}')");

        Assert.False(held);
    }

    [Theory]
    [InlineData("audit_log", "SELECT")]
    [InlineData("audit_log", "INSERT")]
    [InlineData("audit_log_legacy", "SELECT")]
    [InlineData("audit_chain_head", "SELECT")]
    [InlineData("audit_chain_head", "UPDATE")]
    public async Task The_app_role_holds_exactly_what_appending_needs(string table, string privilege)
    {
        var owner = await OwnerAsync();

        var held = await PostgresFixture.ScalarAsync<bool>(owner, $"SELECT has_table_privilege('{PostgresFixture.AppRole}', '{table}', '{privilege}')");

        Assert.True(held);
    }

    [Fact]
    public async Task The_app_role_cannot_switch_the_protection_off()
    {
        var owner = await OwnerAsync();
        var app = PostgresFixture.AsAppRole(owner);

        var disable = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, "ALTER TABLE audit_log DISABLE TRIGGER USER"));
        var drop = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, "DROP TRIGGER audit_log_no_truncate ON audit_log"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, disable.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, drop.SqlState);
    }

    [Fact]
    public async Task Two_entries_cannot_share_a_sequence_number()
    {
        var owner = await OwnerAsync();
        await PostgresFixture.ExecuteAsync(owner, InsertAudit(5));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, InsertAudit(5)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task The_chain_head_starts_at_genesis_and_there_can_only_be_one()
    {
        var owner = await OwnerAsync();

        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM audit_chain_head"));
        Assert.Equal(0, await PostgresFixture.ScalarAsync<long>(owner, "SELECT last_sequence FROM audit_chain_head"));
        Assert.Equal(new string('0', 64), await PostgresFixture.ScalarAsync<string>(owner, "SELECT last_hash FROM audit_chain_head"));

        var second = await Assert.ThrowsAsync<PostgresException>(
            () => PostgresFixture.ExecuteAsync(owner, $"INSERT INTO audit_chain_head (id, last_sequence, last_hash, legacy_imported) VALUES (2, 0, '{new string('0', 64)}', false)"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, second.SqlState);
    }
}
