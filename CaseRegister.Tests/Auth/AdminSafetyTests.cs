using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>The safety limits: there are never fewer than two active admins, however the change is reached.</summary>
[Collection(PostgresTestGroup.Name)]
public class AdminSafetyTests(PostgresFixture postgres)
{
    private static string[] Codes(IEnumerable<ApprovalViolation>? problems) => [.. (problems ?? []).Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    private static string[] Codes(IEnumerable<RoleRuleViolation> problems) => [.. problems.Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    private static Task<int> ActiveAdmins(ApiFactory factory)
    {
        return factory.WithDbAsync(async db =>
        {
            using var scope = factory.Services.CreateScope();

            return await AdminCensus.CountActiveAsync(db, CancellationToken.None);
        });
    }

    private static Task<int> SuspendInTheDatabaseAsync(ApiFactory factory, ApplicationUser user) => factory.WithDbAsync(async db =>
    {
        // As the lockout rule does after repeated lockouts: no admin involved.
        (await db.Users.SingleAsync(u => u.Id == user.Id)).AccountStatus = AccountStatus.Suspended;
        await db.SaveChangesAsync();

        return 0;
    });

    // --- Proposals that would leave fewer than two ----------------------------------------------------------

    [Fact]
    public async Task With_two_admins_neither_can_be_suspended_or_lose_the_admin_role_even_by_both()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        var suspend = await factory.ProposeAsync(admins[0], Proposals.ForUser(PendingActionType.SuspendUser, admins[1]));
        var remove = await factory.ProposeAsync(admins[0], Proposals.RemoveRole(admins[1], Role.Admin));
        var other = await factory.ProposeAsync(admins[1], Proposals.ForUser(PendingActionType.SuspendUser, admins[0]));

        foreach (var result in new[] { suspend, remove, other })
        {
            Assert.Equal(ProposeOutcome.Refused, result.Outcome);
            Assert.Equal([ApprovalRules.MinimumAdmins], Codes(result.Problems));
        }

        Assert.Equal(2, await ActiveAdmins(factory));
        Assert.Equal(0, await factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task With_three_admins_one_can_go_and_then_no_more()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);

        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.ForUser(PendingActionType.SuspendUser, admins[2]));

        Assert.Equal(2, await ActiveAdmins(factory));
        foreach (var command in new[]
        {
            Proposals.ForUser(PendingActionType.SuspendUser, admins[1]),
            Proposals.RemoveRole(admins[1], Role.Admin),
        })
        {
            var result = await factory.ProposeAsync(admins[0], command);
            Assert.Equal(ProposeOutcome.Refused, result.Outcome);
            Assert.Equal([ApprovalRules.MinimumAdmins], Codes(result.Problems));
        }
    }

    [Fact]
    public async Task Taking_the_admin_role_from_a_third_admin_is_allowed_by_two_and_leaves_two()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);

        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RemoveRole(admins[2], Role.Admin));

        Assert.Empty(await factory.RolesOfAsync(admins[2]));
        Assert.Equal(2, await ActiveAdmins(factory));
    }

    [Fact]
    public async Task A_suspended_admin_does_not_count_so_the_others_become_untouchable()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        await SuspendInTheDatabaseAsync(factory, admins[2]);

        var result = await factory.ProposeAsync(admins[0], Proposals.ForUser(PendingActionType.SuspendUser, admins[1]));

        Assert.Equal(2, await ActiveAdmins(factory));
        Assert.Equal(ProposeOutcome.Refused, result.Outcome);
        Assert.Equal([ApprovalRules.MinimumAdmins], Codes(result.Problems));
    }

    [Fact]
    public async Task Removing_the_admin_role_from_someone_who_is_not_an_active_admin_loses_nobody()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        await SuspendInTheDatabaseAsync(factory, admins[2]);

        var result = await factory.TryChangeRolesAsync(new RoleChange(admins[2].Id, [], [Role.Admin]));

        Assert.True(result.Succeeded);
        Assert.Equal(2, await ActiveAdmins(factory));
    }

    // --- The role service enforces it too, however it is reached ----------------------------------------------

    [Fact]
    public async Task The_role_service_itself_refuses_to_leave_fewer_than_two_active_admins()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        var result = await factory.TryChangeRolesAsync(new RoleChange(admins[1].Id, [], [Role.Admin]));

        Assert.False(result.Succeeded);
        Assert.Equal([ApprovalRules.MinimumAdmins], Codes(result.Violations));
        Assert.Equal([Role.Admin], await factory.RolesOfAsync(admins[1]));
        Assert.Equal(2, await ActiveAdmins(factory));
    }

    [Fact]
    public async Task Adding_admins_is_never_held_back_by_the_minimum()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var first = await factory.CreateStaffAsync("first@example.test", Role.Admin); // from nought to one
        var second = await factory.CreateStaffAsync("second@example.test", Role.Admin);

        Assert.Equal(2, await ActiveAdmins(factory));
        Assert.NotEqual(first.Id, second.Id);
    }

    // --- Checked again at confirmation ----------------------------------------------------------------------

    [Fact]
    public async Task The_minimum_is_checked_again_at_confirmation_and_the_proposal_stays_pending()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);

        // Fine when proposed: three admins, one to go.
        var proposal = await factory.ProposeOkAsync(admins[0], Proposals.ForUser(PendingActionType.SuspendUser, admins[2]));

        // Then the proposer is suspended by the lockout rule, so only two active admins are left.
        await SuspendInTheDatabaseAsync(factory, admins[0]);
        var result = await factory.ConfirmAsync(admins[1], proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Contains(ApprovalRules.MinimumAdmins, Codes(result.Problems));
        Assert.Contains("ProposerNoLongerAdmin", Codes(result.Problems));
        Assert.Equal(AccountStatus.Approved, (await factory.GetUserAsync(admins[2].Email!)).AccountStatus);
        Assert.Equal(PendingActionStatus.Pending, (await factory.GetAsync(proposal.Id)).Status);
        Assert.Equal(ApprovalRules.MinimumAdmins, (await factory.GetAsync(proposal.Id)).LastRefusalCode is { } code && code is not null ? ApprovalRules.MinimumAdmins : null);
    }

    [Fact]
    public async Task Each_operation_re_checks_the_minimum_on_its_own()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        using var scope = factory.Services.CreateScope();
        var operations = scope.ServiceProvider.GetServices<IAdminOperation>().ToDictionary(operation => operation.Type);

        var suspend = await operations[PendingActionType.SuspendUser].PlanAsync(new OperationContext(admins[0].Id, admins[1].Id, null, ActionPayloads.Empty), CancellationToken.None);
        var remove = await operations[PendingActionType.RemoveRole].PlanAsync(
            new OperationContext(admins[0].Id, admins[1].Id, null, ActionPayloads.Build(Proposals.RemoveRole(admins[1], Role.Admin))),
            CancellationToken.None);

        Assert.Equal([ApprovalRules.MinimumAdmins], Codes(suspend.Problems));
        Assert.Equal([ApprovalRules.MinimumAdmins], Codes(remove.Problems));
    }

    [Fact]
    public async Task Two_simultaneous_confirmations_that_would_each_leave_two_admins_cannot_both_go_through()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var (a, b, c) = (admins[0], admins[1], admins[2]);

        for (var round = 0; round < 10; round++)
        {
            // Each is fine alone: suspending C leaves A and B; taking Admin from B leaves A and C. Together they leave only A.
            var suspendC = await factory.ProposeOkAsync(a, Proposals.ForUser(PendingActionType.SuspendUser, c));
            var removeB = await factory.ProposeOkAsync(a, Proposals.RemoveRole(b, Role.Admin));
            var codeB = factory.NextTotpCode(b);
            var codeC = factory.NextTotpCode(c);
            var gate = new TaskCompletionSource();

            Task<ConfirmResult> Confirm(ApplicationUser by, PendingActionView proposal, string code) => Task.Run(async () =>
            {
                await gate.Task;
                using var scope = factory.Services.CreateScope();

                return await scope.ServiceProvider.GetRequiredService<IPendingActionService>()
                    .ConfirmAsync(proposal.Id, by.Id, new ConfirmActionCommand(proposal.PayloadHash, code), null);
            });

            var first = Confirm(b, suspendC, codeB);
            var second = Confirm(c, removeB, codeC);
            gate.SetResult();
            var results = await Task.WhenAll(first, second);

            // Exactly one goes through and the other is refused for the number of admins: never a different answer, whoever is first.
            var outcomes = results.Select(result => result.Outcome).Order().ToList();
            Assert.True(outcomes.SequenceEqual([ConfirmOutcome.Confirmed, ConfirmOutcome.Refused]), $"round {round}: {string.Join(", ", outcomes)}");
            Assert.Equal(2, await ActiveAdmins(factory));

            // Put things back for the next round, by hand.
            await factory.WithDbAsync(async db =>
            {
                (await db.Users.SingleAsync(u => u.Id == c.Id)).AccountStatus = AccountStatus.Approved;
                await db.SaveChangesAsync();

                return 0;
            });
            if (!(await factory.RolesOfAsync(b)).Contains(Role.Admin))
            {
                await factory.AssignRolesAsync(b, Role.Admin);
            }

            await factory.RejectAsync(b, await factory.GetAsync(suspendC.Id)); // not C: it is the target of this one
            await factory.RejectAsync(c, await factory.GetAsync(removeB.Id)); // not B: it is the target of this one
            Assert.Equal(3, await ActiveAdmins(factory));
        }
    }

    // --- Structure: granting access can never be done at once -------------------------------------------------

    [Fact]
    public void The_immediate_service_offers_only_actions_that_take_access_away()
    {
        var methods = typeof(IAccountAdministrationService).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal);

        Assert.Equal(["EndSessionsAsync", "SuspendAgencyAsync", "SuspendUserAsync"], methods);
    }

    [Fact]
    public void No_controller_can_change_a_role_verify_a_certificate_or_create_an_admin_without_the_approval_process()
    {
        var forbidden = new[] { typeof(IRoleAssignmentService), typeof(IAdminSeeder), typeof(IAdminOperation) };
        var controllers = typeof(Program).Assembly.GetTypes().Where(type => type.Name.EndsWith("Controller", StringComparison.Ordinal));

        foreach (var controller in controllers)
        {
            var dependencies = controller.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Select(parameter => parameter.ParameterType);

            Assert.Empty(dependencies.Intersect(forbidden));
        }

        Assert.DoesNotContain(
            typeof(CaseRegister.Application.Accounts.IPractisingCertificateService).GetMethods(),
            method => method.Name.Contains("Verify", StringComparison.Ordinal));
        Assert.NotEmpty(SolutionGraph.SolutionRoot);
    }
}
