using System.Globalization;
using CaseRegister.Tests.Architecture;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>What the application's database role may and may not do to proposals and to two-factor records.</summary>
[Collection(PostgresTestGroup.Name)]
public class ApprovalGrantsTests(PostgresFixture postgres)
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
            VALUES ('{id}', 'Seeded Person', 'Approved', false, false, false, true, 0)
            """);

        return id;
    }

    private static async Task<Guid> OwnerInsertsAgencyAsync(string owner)
    {
        var id = Guid.NewGuid();
        await PostgresFixture.ExecuteAsync(owner, $"INSERT INTO agencies (id, name, status, created_at, updated_at) VALUES ('{id}', 'Agency', 'Active', now(), now())");

        return id;
    }

    private static async Task<Guid> OwnerInsertsProposalAsync(
        string owner,
        Guid proposer,
        Guid? targetUser,
        Guid? targetAgency = null,
        string status = "Pending",
        string type = "ResetTwoFactor")
    {
        var id = Guid.NewGuid();
        var user = targetUser is { } u ? $"'{u}'" : "NULL";
        var agency = targetAgency is { } a ? $"'{a}'" : "NULL";
        var hash = new string('a', 64);
        var empty = "{}";

        await PostgresFixture.ExecuteAsync(
            owner,
            $"""
            INSERT INTO pending_actions (id, type, target_user_id, target_agency_id, payload, payload_hash, reason, proposed_by_user_id, proposed_at, expires_at, status, created_at, updated_at)
            VALUES ('{id}', '{type}', {user}, {agency}, '{empty}', '{hash}', 'A reason that is long enough.', '{proposer}', now(), now() + interval '72 hours', '{status}', now(), now())
            """);

        return id;
    }

    private static async Task AssertDeniedAsync(string connection, string statement)
    {
        var denied = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(connection, statement));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    // --- Proposals are exactly what was proposed ---------------------------------------------------------

    [Fact]
    public async Task The_app_role_can_add_a_proposal_and_record_its_outcome_but_change_nothing_else_about_it()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        var checker = await OwnerInsertsUserAsync(owner);
        var target = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);

        var empty = "{}";
        var secondHash = new string('b', 64);
        // Adding is allowed.
        await PostgresFixture.ExecuteAsync(
            app,
            $"""
            INSERT INTO pending_actions (id, type, target_user_id, payload, payload_hash, reason, proposed_by_user_id, proposed_at, expires_at, status, created_at, updated_at)
            VALUES (gen_random_uuid(), 'EndSessions', '{target}', '{empty}', '{secondHash}', 'A reason that is long enough.', '{maker}', now(), now() + interval '72 hours', 'Pending', now(), now())
            """);

        var id = await OwnerInsertsProposalAsync(owner, maker, target);

        // Recording the outcome is allowed...
        await PostgresFixture.ExecuteAsync(app, $"UPDATE pending_actions SET status = 'Confirmed', decided_by_user_id = '{checker}', decided_at = now(), decision_reason = 'ok', updated_at = now() WHERE id = '{id}'");
        await PostgresFixture.ExecuteAsync(app, $"UPDATE pending_actions SET last_refusal_code = 'X', last_refusal_message = 'y', last_refused_at = now() WHERE id = '{id}'");

        // ...and everything that makes it the proposal it is, is not.
        foreach (var change in new[]
        {
            "type = 'AssignRole'",
            $"target_user_id = '{checker}'",
            "target_user_id = NULL",
            "payload = '{\"role\":\"Admin\"}'",
            $"payload_hash = '{new string('c', 64)}'",
            "reason = 'Another reason entirely.'",
            $"proposed_by_user_id = '{checker}'",
            "proposed_at = now()",
            "expires_at = now() + interval '1 year'",
            "created_at = now()",
        })
        {
            await AssertDeniedAsync(app, $"UPDATE pending_actions SET {change} WHERE id = '{id}'");
        }
    }

    [Fact]
    public async Task A_proposal_can_never_be_deleted_by_the_app_role()
    {
        var owner = await MigratedWithGrantsAsync();
        var id = await OwnerInsertsProposalAsync(owner, await OwnerInsertsUserAsync(owner), await OwnerInsertsUserAsync(owner));
        var app = PostgresFixture.AsAppRole(owner);

        await AssertDeniedAsync(app, $"DELETE FROM pending_actions WHERE id = '{id}'");
        await AssertDeniedAsync(app, "TRUNCATE pending_actions");
        Assert.Equal(1, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM pending_actions"));
    }

    [Fact]
    public async Task The_database_allows_only_one_pending_proposal_per_action_type_and_target()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        var target = await OwnerInsertsUserAsync(owner);
        var other = await OwnerInsertsUserAsync(owner);
        var agency = await OwnerInsertsAgencyAsync(owner);
        await OwnerInsertsProposalAsync(owner, maker, target);
        await OwnerInsertsProposalAsync(owner, maker, null, agency, type: "ReactivateAgency");

        // The same type and the same target again, user or agency (the empty other column counts as equal).
        foreach (var duplicate in new Func<Task>[]
        {
            () => OwnerInsertsProposalAsync(owner, other, target),
            () => OwnerInsertsProposalAsync(owner, other, null, agency, type: "ReactivateAgency"),
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(duplicate);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);
            Assert.Equal("ux_pending_actions_one_pending_per_target", refused.ConstraintName);
        }

        // A different type, a different target, or one that is no longer pending is fine.
        await OwnerInsertsProposalAsync(owner, maker, target, type: "EndSessions");
        await OwnerInsertsProposalAsync(owner, maker, other);
        await OwnerInsertsProposalAsync(owner, maker, target, status: "Rejected");
        await OwnerInsertsProposalAsync(owner, maker, target, status: "Expired");
        Assert.Equal(6, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM pending_actions"));
    }

    [Fact]
    public async Task A_proposal_targets_exactly_one_user_or_one_agency()
    {
        var owner = await MigratedWithGrantsAsync();
        var maker = await OwnerInsertsUserAsync(owner);
        var target = await OwnerInsertsUserAsync(owner);
        var agency = await OwnerInsertsAgencyAsync(owner);

        foreach (var invalid in new Func<Task>[]
        {
            () => OwnerInsertsProposalAsync(owner, maker, null),
            () => OwnerInsertsProposalAsync(owner, maker, target, agency),
        })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(invalid);
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Equal("ck_pending_actions_one_target", refused.ConstraintName);
        }
    }

    // --- Two-factor resets keep the history ----------------------------------------------------------------

    [Fact]
    public async Task An_authenticator_is_marked_removed_never_deleted_and_a_new_one_can_follow()
    {
        var owner = await MigratedWithGrantsAsync();
        var user = await OwnerInsertsUserAsync(owner);
        var app = PostgresFixture.AsAppRole(owner);
        var insert = string.Create(
            CultureInfo.InvariantCulture,
            $"INSERT INTO user_authenticators (id, user_id, encrypted_key, created_at, updated_at) VALUES (gen_random_uuid(), '{user}', 'key', now(), now())");

        await PostgresFixture.ExecuteAsync(app, insert);

        // Only one live authenticator per account.
        var second = await Assert.ThrowsAsync<PostgresException>(() => PostgresFixture.ExecuteAsync(app, insert));
        Assert.Equal("ux_user_authenticators_live", second.ConstraintName);

        // Removing is marking; deleting is not allowed.
        await AssertDeniedAsync(app, "DELETE FROM user_authenticators");
        await PostgresFixture.ExecuteAsync(app, $"UPDATE user_authenticators SET removed_at = now() WHERE user_id = '{user}'");

        // After a reset, a new one can be added, and the old one is still there.
        await PostgresFixture.ExecuteAsync(app, insert);
        Assert.Equal(2, await PostgresFixture.ScalarAsync<long>(owner, "SELECT count(*) FROM user_authenticators"));
    }
}
