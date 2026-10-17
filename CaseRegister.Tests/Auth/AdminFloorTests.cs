using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Api.Cli;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CaseRegister.Tests.Auth;

/// <summary>
/// The floor of two active admins, how a third admin arrives, the one way below the floor (a break-glass command), and the admin list's warning.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AdminFloorTests(PostgresFixture postgres)
{
    private const string Reason = "The admin left the association and their laptop was lost; confirmed with the chairperson.";

    private static IConfiguration ConfigurationFor(ApiFactory factory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"],
            ["ConnectionStrings:Migrations"] = factory.OwnerConnectionString,
        }).Build();

    private static async Task<NpgsqlConnection> OwnerConnectionAsync(ApiFactory factory)
    {
        var connection = new NpgsqlConnection(factory.OwnerConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    private static Task<int> SuspendAdminAsync(ApiFactory factory, ScriptedTerminal terminal) =>
        BreakGlassCommands.RunAsync([BreakGlassCommands.SuspendAdminName], factory.Services, ConfigurationFor(factory), terminal);

    private static ScriptedTerminal Answers(string email, string reason = Reason) => new([email, reason, email], []);

    // --- A third admin arrives by maker-checker, not by the command ---------------------------------------------------

    [Fact]
    public async Task Seed_admin_stops_at_two_and_a_third_admin_comes_through_the_approval_process()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        // The command refuses once two admins exist.
        var terminal = new ScriptedTerminal(["third@example.test", "Third Admin", "yes"], ["purple tractor sings loudly", "purple tractor sings loudly"]);
        var exitCode = await SeedAdminCommand.RunAsync([SeedAdminCommand.Name], factory.Services, ConfigurationFor(factory), terminal);
        Assert.Equal(SeedAdminCommand.Refused, exitCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Email == "third@example.test")));

        // The existing grant: one admin proposes the Admin role for a staff member, the other confirms.
        var staff = await factory.CreateUserAsync("newadmin@example.test", staff: true);
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.AssignRole(staff, Role.Admin));

        Assert.Contains(Role.Admin, await factory.RolesOfAsync(staff));
        Assert.Equal(3, await factory.WithDbAsync(db => AdminCensus.CountActiveAsync(db, CancellationToken.None)));
    }

    // --- The admin rows are locked while the set of admins is changed ----------------------------------------------------

    [Fact]
    public async Task While_the_set_of_admins_is_being_changed_the_admin_rows_are_locked()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var member = await factory.CreateUserAsync("plain@example.test");

        await using var holder = await OwnerConnectionAsync(factory);
        await using var transaction = await holder.BeginTransactionAsync();

        // The same lock the confirming transaction takes: the advisory lock, then SELECT ... FOR UPDATE on the admin rows.
        await using (var command = new NpgsqlCommand(
            "SELECT 1 FROM users u WHERE u.id IN (SELECT ur.user_id FROM user_roles ur WHERE ur.role_id = @role) FOR UPDATE OF u", holder, transaction))
        {
            command.Parameters.AddWithValue("role", CaseRegister.Infrastructure.Persistence.RoleSeed.IdOf(Role.Admin));
            await command.ExecuteNonQueryAsync();
        }

        // Another transaction cannot change an admin row meanwhile, but a person who is not an admin is not held up.
        Assert.Equal(PostgresErrorCodes.LockNotAvailable, await TryUpdateAsync(factory, admins[0].Id));
        Assert.Null(await TryUpdateAsync(factory, member.Id));

        // And the real lock takes exactly that, under the real code path.
        await transaction.RollbackAsync();
        await factory.WithDbAsync(async db =>
        {
            await using var inner = await db.Database.BeginTransactionAsync();
            await AdminCensus.LockAsync(db, CancellationToken.None);
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, await TryUpdateAsync(factory, admins[1].Id));
            await inner.RollbackAsync();

            return 0;
        });
    }

    private static async Task<string?> TryUpdateAsync(ApiFactory factory, Guid userId)
    {
        await using var connection = await OwnerConnectionAsync(factory);
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var timeout = new NpgsqlCommand("SET LOCAL lock_timeout = '300ms'", connection, transaction))
        {
            await timeout.ExecuteNonQueryAsync();
        }

        try
        {
            await using var update = new NpgsqlCommand("UPDATE users SET full_name = full_name WHERE id = @id", connection, transaction);
            update.Parameters.AddWithValue("id", userId);
            await update.ExecuteNonQueryAsync();

            return null;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    // --- Break-glass suspend-admin ---------------------------------------------------------------------------------

    [Fact]
    public async Task Suspend_admin_is_the_only_way_below_two_and_it_is_audited_and_announced()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var gone = admins[1];
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, gone);
        var terminal = Answers(gone.Email!);

        // The normal routes refuse: this is exactly what the floor is for.
        Assert.Equal(ProposeOutcome.Refused, (await factory.ProposeAsync(admins[0], Proposals.ForUser(PendingActionType.SuspendUser, gone))).Outcome);

        var exitCode = await SuspendAdminAsync(factory, terminal);

        Assert.Equal(BreakGlassCommands.Succeeded, exitCode);
        Assert.Equal(AccountStatus.Suspended, (await factory.GetUserAsync(gone.Email!)).AccountStatus);
        Assert.Equal(1, await factory.WithDbAsync(db => AdminCensus.CountActiveAsync(db, CancellationToken.None)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Sessions.CountAsync(s => s.UserId == gone.Id)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        var entries = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "BreakGlassUsed" || e.Action == "UserSuspended").ToListAsync());
        var used = Assert.Single(entries, e => e.Action == "BreakGlassUsed");
        Assert.Equal(gone.Id.ToString(), used.Subject);
        Assert.Null(used.ActorUserId);
        Assert.Contains("suspend-admin", used.Detail, StringComparison.Ordinal);
        Assert.Contains(Reason, used.Detail, StringComparison.Ordinal);
        Assert.Single(entries, e => e.Action == "UserSuspended");

        // The remaining admin and the person are told, critically.
        foreach (var recipient in new[] { admins[0], gone })
        {
            var mine = await factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientId == recipient.Id && n.Type == NotificationType.BreakGlass).ToListAsync());
            Assert.Equal(NotificationSeverity.Critical, Assert.Single(mine).Severity);
        }
    }

    [Fact]
    public async Task Suspend_admin_refuses_someone_who_is_not_an_admin_and_changes_nothing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateAdminsAsync(2);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var terminal = Answers(member.Email!);

        Assert.Equal(BreakGlassCommands.Refused, await SuspendAdminAsync(factory, terminal));

        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(member.Email!)).AccountStatus);
        Assert.Equal(0, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "BreakGlassUsed")));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "BreakGlassRefused")));
    }

    [Fact]
    public async Task Suspend_admin_shares_the_one_per_person_per_24_hours_limit()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var target = admins[2];

        // A reset of this person's two-factor counts as the day's break-glass action on them.
        Assert.Equal(BreakGlassCommands.Succeeded, await BreakGlassCommands.RunAsync(
            [BreakGlassCommands.ResetTwoFactorName], factory.Services, ConfigurationFor(factory), Answers(target.Email!)));

        var terminal = Answers(target.Email!);
        Assert.Equal(BreakGlassCommands.Refused, await SuspendAdminAsync(factory, terminal));
        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(target.Email!)).AccountStatus);
        Assert.Contains(terminal.Output, line => line.Contains("last 24 hours", StringComparison.Ordinal));
    }

    // --- The admin list and its warning ----------------------------------------------------------------------------------

    private static async Task<JsonElement> ListAsync(ApiClient client)
    {
        var response = await client.GetAsync("/api/admin/admins");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_admin_list_warns_while_there_are_fewer_than_three_active_admins()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, admins[0]);

        var two = await ListAsync(client);
        Assert.True(two.GetProperty("warning").GetBoolean());
        Assert.Equal(2, two.GetProperty("activeCount").GetInt32());
        Assert.Equal(2, two.GetProperty("minimum").GetInt32());
        Assert.Equal(3, two.GetProperty("recommended").GetInt32());
        Assert.Equal(2, two.GetProperty("admins").GetArrayLength());

        var staff = await factory.CreateStaffAsync("third@example.test", Role.Admin);
        var three = await ListAsync(client);
        Assert.False(three.GetProperty("warning").GetBoolean());
        Assert.Equal(3, three.GetProperty("admins").GetArrayLength());
        Assert.Contains(three.GetProperty("admins").EnumerateArray(), admin => admin.GetProperty("id").GetGuid() == staff.Id);
    }

    [Fact]
    public async Task A_suspended_admin_is_listed_but_does_not_count()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(4);
        await factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == admins[3].Id)).AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, admins[0]);

        var list = await ListAsync(client);

        Assert.Equal(4, list.GetProperty("admins").GetArrayLength());
        Assert.Equal(3, list.GetProperty("activeCount").GetInt32());
        Assert.False(list.GetProperty("warning").GetBoolean());
        Assert.Contains(list.GetProperty("admins").EnumerateArray(), admin => !admin.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Only_admins_can_see_the_admin_list_and_it_names_nobody_else()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateAdminsAsync(3);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var client = factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(factory, member);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/admins")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().GetAsync("/api/admin/admins")).StatusCode);
    }
}
