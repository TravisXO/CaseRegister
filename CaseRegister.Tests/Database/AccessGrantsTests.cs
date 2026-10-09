using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Architecture;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>What the application's database role may and may not do to the roles, agencies and certificates tables.</summary>
[Collection(PostgresTestGroup.Name)]
public class AccessGrantsTests(PostgresFixture postgres)
{
    private static readonly string _adminRoleId = RoleSeed.IdOf(Role.Admin).ToString();

    private async Task<string> MigratedWithGrantsAsync()
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, await File.ReadAllTextAsync(Path.Combine(SolutionGraph.SolutionRoot, "database", "app-role-grants.sql")));

        return owner;
    }

    private static async Task<Guid> OwnerInsertsUserAsync(string owner)
    {
        var id = Guid.NewGuid();
        await PostgresFixture.ExecuteAsync(
            owner,
            $"""
            INSERT INTO users (id, full_name, account_status, email_confirmed, phone_number_confirmed, two_factor_enabled, lockout_enabled, access_failed_count)
            VALUES ('{id}', 'Seeded Member', 'Approved', false, false, false, true, 0)
            """);

        return id;
    }

    private static async Task<Guid> OwnerInsertsCertificateAsync(string owner, Guid userId)
    {
        var id = Guid.NewGuid();
        await PostgresFixture.ExecuteAsync(
            owner,
            $"""
            INSERT INTO practising_certificates (id, user_id, certificate_number, valid_from, valid_to, created_at, updated_at)
            VALUES ('{id}', '{userId}', '9611/2026', '2026-01-01', '2026-12-31', now(), now())
            """);

        return id;
    }

    private static async Task AssertDeniedAsync(string connection, string statement)
    {
        var denied = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(connection, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    // --- The roles come from the migration -----------------------------------------------------------

    [Fact]
    public async Task Migrating_an_empty_database_puts_the_four_roles_in_it()
    {
        var owner = await postgres.CreateDatabaseAsync();

        await DatabaseMigrator.MigrateAsync(owner);

        var names = await PostgresFixture.ScalarAsync<string>(owner, "SELECT string_agg(name, ',' ORDER BY name) FROM roles");
        Assert.Equal("Admin,AgencyComplianceOfficer,CaseReviewer,RegisteredAgent", names);
        Assert.Equal(_adminRoleId, (await PostgresFixture.ScalarAsync<Guid>(owner, "SELECT id FROM roles WHERE name = 'Admin'")).ToString());
    }

    [Fact]
    public async Task The_seeded_roles_are_stored_the_way_identity_looks_them_up()
    {
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);

        var normalised = await PostgresFixture.ScalarAsync<string>(owner, "SELECT string_agg(normalized_name, ',' ORDER BY normalized_name) FROM roles");

        Assert.Equal("admin,agencycomplianceofficer,casereviewer,registeredagent", normalised);
    }

    // --- The application cannot change the roles ------------------------------------------------------

    [Theory]
    [InlineData("INSERT INTO roles (id, name, normalized_name, concurrency_stamp) VALUES (gen_random_uuid(), 'Intruder', 'intruder', 'x')")]
    [InlineData("UPDATE roles SET name = 'Renamed'")]
    [InlineData("DELETE FROM roles")]
    [InlineData("TRUNCATE roles CASCADE")]
    [InlineData("INSERT INTO role_claims (role_id, claim_type, claim_value) VALUES ('01990000-0000-7000-8000-000000000004', 'x', 'y')")]
    [InlineData("UPDATE role_claims SET claim_value = 'y'")]
    [InlineData("DELETE FROM role_claims")]
    public async Task The_app_role_cannot_create_rename_or_remove_a_role(string statement)
    {
        var owner = await MigratedWithGrantsAsync();

        await AssertDeniedAsync(PostgresFixture.AsAppRole(owner), statement);
    }

    [Fact]
    public async Task The_app_role_can_read_the_roles()
    {
        var owner = await MigratedWithGrantsAsync();

        Assert.Equal(4, await PostgresFixture.ScalarAsync<long>(PostgresFixture.AsAppRole(owner), "SELECT count(*) FROM roles"));
    }

    [Fact]
    public async Task The_app_role_can_assign_and_unassign_roles_to_users()
    {
        var owner = await MigratedWithGrantsAsync();
        var userId = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(app, $"INSERT INTO user_roles (user_id, role_id) VALUES ('{userId}', '{_adminRoleId}')");
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(app, "SELECT count(*) FROM user_roles"));
        await PostgresFixture.ExecuteAsync(app, $"DELETE FROM user_roles WHERE user_id = '{userId}'");
        Assert.Equal(0, await PostgresFixture.ScalarAsync<long>(app, "SELECT count(*) FROM user_roles"));
    }

    // --- Agencies are kept ----------------------------------------------------------------------------

    [Fact]
    public async Task The_app_role_can_add_and_change_agencies_but_never_delete_one()
    {
        var owner = await MigratedWithGrantsAsync();
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(app, "INSERT INTO agencies (id, name, status, created_at, updated_at) VALUES (gen_random_uuid(), 'Agency', 'Active', now(), now())");
        await PostgresFixture.ExecuteAsync(app, "UPDATE agencies SET status = 'Suspended'");

        await AssertDeniedAsync(app, "DELETE FROM agencies");
        await AssertDeniedAsync(app, "TRUNCATE agencies CASCADE");
    }

    // --- Certificates are history -----------------------------------------------------------------------

    [Fact]
    public async Task The_app_role_can_add_a_certificate_and_read_it()
    {
        var owner = await MigratedWithGrantsAsync();
        var userId = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(
            app,
            $"INSERT INTO practising_certificates (id, user_id, certificate_number, valid_from, valid_to, created_at, updated_at) VALUES (gen_random_uuid(), '{userId}', '1/2026', '2026-01-01', '2026-12-31', now(), now())");

        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(app, "SELECT count(*) FROM practising_certificates"));
    }

    [Fact]
    public async Task The_app_role_can_verify_a_certificate_and_nothing_else_about_it()
    {
        var owner = await MigratedWithGrantsAsync();
        var userId = await OwnerInsertsUserAsync(owner);
        var certificateId = await OwnerInsertsCertificateAsync(owner, userId);
        var app = PostgresFixture.AsAppRole(owner);

        // The verification is allowed...
        await PostgresFixture.ExecuteAsync(app, $"UPDATE practising_certificates SET verified_at = now(), verified_by_user_id = '{userId}', updated_at = now() WHERE id = '{certificateId}'");

        // ...and everything that makes it the certificate it is, is not.
        foreach (var column in new[] { "certificate_number = '5/2026'", "valid_from = '2026-02-01'", "valid_to = '2027-12-31'", $"user_id = '{Guid.NewGuid()}'", "created_at = now()" })
        {
            await AssertDeniedAsync(app, $"UPDATE practising_certificates SET {column} WHERE id = '{certificateId}'");
        }
    }

    [Fact]
    public async Task A_certificate_can_never_be_deleted_by_the_app_role()
    {
        var owner = await MigratedWithGrantsAsync();
        var certificateId = await OwnerInsertsCertificateAsync(owner, await OwnerInsertsUserAsync(owner));
        var app = PostgresFixture.AsAppRole(owner);

        await AssertDeniedAsync(app, $"DELETE FROM practising_certificates WHERE id = '{certificateId}'");
        await AssertDeniedAsync(app, "TRUNCATE practising_certificates");
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM practising_certificates"));
    }

    [Fact]
    public async Task A_certificate_number_can_only_exist_once_in_the_database()
    {
        var owner = await MigratedWithGrantsAsync();
        var userId = await OwnerInsertsUserAsync(owner);
        await OwnerInsertsCertificateAsync(owner, userId);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => OwnerInsertsCertificateAsync(owner, userId));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("ux_practising_certificates_number", duplicate.ConstraintName);
    }

    [Fact]
    public async Task The_grant_script_can_be_run_again_after_these_changes()
    {
        var owner = await MigratedWithGrantsAsync();

        await PostgresFixture.ExecuteAsync(owner, await File.ReadAllTextAsync(Path.Combine(SolutionGraph.SolutionRoot, "database", "app-role-grants.sql")));

        Assert.Equal(4, await PostgresFixture.ScalarAsync<long>(PostgresFixture.AsAppRole(owner), "SELECT count(*) FROM roles"));
    }
}
