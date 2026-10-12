using CaseRegister.Tests.Architecture;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>What the application's database role may do to applications, setup tokens and invite proposals.</summary>
[Collection(PostgresTestGroup.Name)]
public class ApplicationGrantsTests(PostgresFixture postgres)
{
    private async Task<string> MigratedWithGrantsAsync()
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await CaseRegister.Infrastructure.Persistence.DatabaseMigrator.MigrateAsync(owner);
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
            VALUES ('{id}', 'Seeded Person', 'Pending', false, false, false, true, 0)
            """);

        return id;
    }

    private static async Task AssertDeniedAsync(string connection, string statement)
    {
        var denied = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(connection, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private static string InsertApplication(Guid userId, int number = 884) =>
        $"""
        INSERT INTO member_applications (id, user_id, membership_number, membership_class, agency_name, certificate_number, flags, status, submitted_at, reapplication_count, created_at, updated_at)
        VALUES (gen_random_uuid(), '{userId}', {number}, 'Full', 'Agency', '9611/2026', 'None', 'Pending', now(), 0, now(), now())
        """;

    private static string InsertToken(Guid userId, Guid createdBy, string hash) =>
        $"""
        INSERT INTO staff_setup_tokens (id, user_id, token_hash, expires_at, created_by_user_id, created_at, updated_at)
        VALUES (gen_random_uuid(), '{userId}', '{hash}', now() + interval '24 hours', '{createdBy}', now(), now())
        """;

    private static string InsertInvite(Guid proposer, string email, string status = "Pending", Guid? alsoTargetUser = null)
    {
        var emailSql = email.Length == 0 ? "NULL" : $"'{email}'";
        var userSql = alsoTargetUser is { } user ? $"'{user}'" : "NULL";
        var empty = "{}";
        var hash = new string('a', 64);

        return $"""
            INSERT INTO pending_actions (id, type, target_user_id, target_agency_id, target_email, payload, payload_hash, reason, proposed_by_user_id, proposed_at, expires_at, status, created_at, updated_at)
            VALUES (gen_random_uuid(), 'InviteStaff', {userSql}, NULL, {emailSql}, '{empty}', '{hash}', 'A reason that is long enough.', '{proposer}', now(), now() + interval '72 hours', '{status}', now(), now())
            """;
    }

    // --- Applications are kept ------------------------------------------------------------------------

    [Fact]
    public async Task The_app_role_can_add_and_change_an_application_but_never_delete_one()
    {
        var owner = await MigratedWithGrantsAsync();
        var user = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);

        await PostgresFixture.ExecuteAsync(app, InsertApplication(user));
        await PostgresFixture.ExecuteAsync(app, $"UPDATE member_applications SET status = 'Rejected', decision_reason = 'No.', updated_at = now() WHERE user_id = '{user}'");

        await AssertDeniedAsync(app, "DELETE FROM member_applications");
        await AssertDeniedAsync(app, "TRUNCATE member_applications");
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM member_applications"));
    }

    [Fact]
    public async Task There_is_one_application_per_applicant_but_a_number_can_be_claimed_twice_so_it_can_be_flagged()
    {
        var owner = await MigratedWithGrantsAsync();
        var first = await OwnerInsertsUserAsync(owner);
        var second = await OwnerInsertsUserAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, InsertApplication(first));

        var again = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, InsertApplication(first, 5)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, again.SqlState);

        await PostgresFixture.ExecuteAsync(owner, InsertApplication(second)); // the same number 884: allowed, and flagged by the application
        Assert.Equal(2, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM member_applications WHERE membership_number = 884"));
    }

    // --- Setup tokens ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_setup_token_can_only_be_marked_used_or_replaced_and_never_changed_or_deleted()
    {
        var owner = await MigratedWithGrantsAsync();
        var user = await OwnerInsertsUserAsync(owner);
        var admin = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);
        var hash = new string('c', 64);

        await PostgresFixture.ExecuteAsync(app, InsertToken(user, admin, hash));

        await PostgresFixture.ExecuteAsync(app, "UPDATE staff_setup_tokens SET used_at = now(), updated_at = now()");
        await PostgresFixture.ExecuteAsync(app, "UPDATE staff_setup_tokens SET revoked_at = now()");
        foreach (var change in new[]
        {
            $"token_hash = '{new string('d', 64)}'",
            "expires_at = now() + interval '1 year'",
            $"user_id = '{admin}'",
            $"created_by_user_id = '{user}'",
            "created_at = now()",
        })
        {
            await AssertDeniedAsync(app, $"UPDATE staff_setup_tokens SET {change}");
        }

        await AssertDeniedAsync(app, "DELETE FROM staff_setup_tokens");
        await AssertDeniedAsync(app, "TRUNCATE staff_setup_tokens");
    }

    [Fact]
    public async Task A_token_hash_can_only_exist_once()
    {
        var owner = await MigratedWithGrantsAsync();
        var user = await OwnerInsertsUserAsync(owner);
        var admin = await OwnerInsertsUserAsync(owner);
        var hash = new string('e', 64);
        await PostgresFixture.ExecuteAsync(owner, InsertToken(user, admin, hash));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, InsertToken(admin, admin, hash)));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    // --- Invite proposals --------------------------------------------------------------------------------

    [Fact]
    public async Task An_invite_names_an_email_as_its_only_target_and_only_one_can_be_pending_for_it()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, InsertInvite(maker, "new@example.test"));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, InsertInvite(maker, "new@example.test")));
        Assert.Equal("ux_pending_actions_one_pending_per_target", duplicate.ConstraintName);

        await PostgresFixture.ExecuteAsync(owner, InsertInvite(maker, "other@example.test")); // another address
        await PostgresFixture.ExecuteAsync(owner, InsertInvite(maker, "new@example.test", status: "Withdrawn")); // no longer pending
    }

    [Fact]
    public async Task A_proposal_still_has_exactly_one_target_of_the_three_kinds()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        var target = await OwnerInsertsUserAsync(owner);

        foreach (var invalid in new[]
        {
            InsertInvite(maker, string.Empty), // none
            InsertInvite(maker, "x@example.test", alsoTargetUser: target), // an email and a user
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(owner, invalid));
            Assert.Equal("ck_pending_actions_one_target", refused.ConstraintName);
        }
    }

    [Fact]
    public async Task The_target_email_of_a_proposal_cannot_be_changed_by_the_app_role()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, InsertInvite(maker, "new@example.test"));
        var app = PostgresFixture.AsAppRole(owner);

        await AssertDeniedAsync(app, "UPDATE pending_actions SET target_email = 'someone.else@example.test'");
        await PostgresFixture.ExecuteAsync(app, "UPDATE pending_actions SET status = 'Withdrawn', updated_at = now()"); // recording the outcome is still allowed
    }
}
