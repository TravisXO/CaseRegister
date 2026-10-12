using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Testing;

/// <summary>The commands the admin endpoints take, written once for the tests.</summary>
public static class Proposals
{
    public const string Reason = "Checked the file and the request is in order.";

    public static ProposeActionCommand AssignRole(ApplicationUser target, Role role) =>
        new(PendingActionType.AssignRole, target.Id, null, role, null, Reason);

    public static ProposeActionCommand RemoveRole(ApplicationUser target, Role role) =>
        new(PendingActionType.RemoveRole, target.Id, null, role, null, Reason);

    public static ProposeActionCommand VerifyCertificate(ApplicationUser owner, Guid certificateId) =>
        new(PendingActionType.VerifyPractisingCertificate, owner.Id, null, null, certificateId, Reason);

    public static ProposeActionCommand ForUser(PendingActionType type, ApplicationUser target) =>
        new(type, target.Id, null, null, null, Reason);

    public static ProposeActionCommand ForAgency(PendingActionType type, Guid agencyId) =>
        new(type, null, agencyId, null, null, Reason);

    public static ProposeActionCommand Approve(ApplicationUser applicant, Guid? existingAgencyId = null, string? newAgencyName = null, params string[] acknowledgedFlags) =>
        new(PendingActionType.ApproveApplication, applicant.Id, null, null, null, Reason, existingAgencyId, newAgencyName, acknowledgedFlags);

    public static ProposeActionCommand RejectApplication(ApplicationUser applicant, string reason = Reason) =>
        new(PendingActionType.RejectApplication, applicant.Id, null, null, null, reason);

    public static ProposeActionCommand Invite(string email, string fullName, Role role) =>
        new(PendingActionType.InviteStaff, null, null, role, null, Reason, InviteEmail: email, InviteFullName: fullName);
}

/// <summary>Drives the approval engine through the real services, with the real two-factor check, the way the endpoints do.</summary>
public static class ApprovalFlows
{
    public static async Task<ProposeResult> ProposeAsync(this ApiFactory factory, ApplicationUser by, ProposeActionCommand command)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().ProposeAsync(by.Id, command, "203.0.113.5");
    }

    /// <summary>Proposes and expects it to be accepted.</summary>
    public static async Task<PendingActionView> ProposeOkAsync(this ApiFactory factory, ApplicationUser by, ProposeActionCommand command)
    {
        var result = await factory.ProposeAsync(by, command);

        Assert.True(
            result.Outcome == ProposeOutcome.Proposed,
            $"{result.Outcome}: {string.Join(", ", (result.Problems ?? []).Select(problem => problem.Code))}");

        return result.Action!;
    }

    /// <summary>Confirms with a fresh code, quoting the hash of the proposal unless told otherwise.</summary>
    public static async Task<ConfirmResult> ConfirmAsync(
        this ApiFactory factory,
        ApplicationUser by,
        PendingActionView proposal,
        string? hash = null,
        string? code = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().ConfirmAsync(
            proposal.Id,
            by.Id,
            new ConfirmActionCommand(hash ?? proposal.PayloadHash, code ?? factory.NextTotpCode(by)),
            "203.0.113.6");
    }

    public static async Task<PendingActionView> ConfirmOkAsync(this ApiFactory factory, ApplicationUser by, PendingActionView proposal)
    {
        var result = await factory.ConfirmAsync(by, proposal);

        Assert.True(
            result.Outcome == ConfirmOutcome.Confirmed,
            $"{result.Outcome}: {string.Join(", ", (result.Problems ?? []).Select(problem => problem.Code))}");

        return result.Action!;
    }


    /// <summary>Confirms and returns the whole answer, including the one-time setup link of an invite.</summary>
    public static async Task<ConfirmResult> ConfirmWithLinkAsync(this ApiFactory factory, ApplicationUser by, PendingActionView proposal)
    {
        var result = await factory.ConfirmAsync(by, proposal);

        Assert.True(
            result.Outcome == ConfirmOutcome.Confirmed,
            $"{result.Outcome}: {string.Join(", ", (result.Problems ?? []).Select(problem => problem.Code))}");

        return result;
    }

    /// <summary>The token in a setup link: everything after the "#".</summary>
    public static string TokenOf(string setupLink) => setupLink[(setupLink.IndexOf('#', StringComparison.Ordinal) + 1)..];
    /// <summary>One admin proposes, a different admin confirms.</summary>
    public static async Task<PendingActionView> ProposeAndConfirmAsync(this ApiFactory factory, ApplicationUser maker, ApplicationUser checker, ProposeActionCommand command) =>
        await factory.ConfirmOkAsync(checker, await factory.ProposeOkAsync(maker, command));

    public static async Task<PendingActionView> GetAsync(this ApiFactory factory, Guid id)
    {
        using var scope = factory.Services.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IPendingActionService>().GetAsync(id))!;
    }

    public static async Task<IReadOnlyList<PendingActionView>> ListAsync(this ApiFactory factory, bool includeClosed = false)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().ListAsync(includeClosed);
    }

    public static async Task<DecisionResult> RejectAsync(this ApiFactory factory, ApplicationUser by, PendingActionView proposal, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().RejectAsync(proposal.Id, by.Id, reason ?? Proposals.Reason, null);
    }

    public static async Task<DecisionResult> WithdrawAsync(this ApiFactory factory, ApplicationUser by, PendingActionView proposal, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().WithdrawAsync(proposal.Id, by.Id, reason ?? Proposals.Reason, null);
    }

    public static async Task<AdminActionResult> SuspendUserAsync(this ApiFactory factory, ApplicationUser by, ApplicationUser target, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountAdministrationService>().SuspendUserAsync(by.Id, target.Id, reason ?? Proposals.Reason, null);
    }

    public static async Task<AdminActionResult> EndSessionsAsync(this ApiFactory factory, ApplicationUser by, ApplicationUser target, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountAdministrationService>().EndSessionsAsync(by.Id, target.Id, reason ?? Proposals.Reason, null);
    }

    public static async Task<AdminActionResult> SuspendAgencyAsync(this ApiFactory factory, ApplicationUser by, Guid agencyId, string? reason = null)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IAccountAdministrationService>().SuspendAgencyAsync(by.Id, agencyId, reason ?? Proposals.Reason, null);
    }

    /// <summary>Several admins, for tests that need a maker, a checker and a spare.</summary>
    public static async Task<ApplicationUser[]> CreateAdminsAsync(this ApiFactory factory, int count)
    {
        var admins = new ApplicationUser[count];
        for (var index = 0; index < count; index++)
        {
            admins[index] = await factory.CreateStaffAsync($"admin{index + 1}@example.test", Role.Admin);
        }

        return admins;
    }
}

/// <summary>Applying as an agent, the way the sign-up page does.</summary>
public static class SignUps
{
    public const string StrongPassword = "purple tractor sings loudly";

    private static int _membershipSequence = 5000;
    private static int _certificateSequence = 9000;

    public static object Body(
        ApiFactory factory,
        string email = "jane@example.test",
        string fullName = "Jane Mwansa",
        string? password = null,
        string? membershipNumber = null,
        string? certificateNumber = null,
        int? year = null,
        string agencyName = "Copperbelt Homes Ltd",
        string membershipClass = "Full",
        string? turnstileToken = FakeTurnstileVerifier.ValidToken)
    {
        var certificateYear = year ?? factory.Today.Year;

        return new
        {
            fullName,
            email,
            password = password ?? StrongPassword,
            membershipNumber = membershipNumber ?? $"AGT {Interlocked.Increment(ref _membershipSequence)}",
            membershipClass,
            agencyName,
            certificateNumber = certificateNumber ?? $"{Interlocked.Increment(ref _certificateSequence)}/{certificateYear}",
            certificateYear,
            turnstileToken,
        };
    }

    public static Task<HttpResponseMessage> PostAsync(ApiClient client, object body, string? forwardedFor = null) =>
        client.SendAsync(HttpMethod.Post, "/api/auth/sign-up", body, forwardedFor: forwardedFor);

    /// <summary>Applies and expects it to be accepted. Returns the applicant's account as it is stored.</summary>
    public static async Task<ApplicationUser> ApplyAsync(ApiFactory factory, string email = "jane@example.test", object? body = null)
    {
        var response = await PostAsync(factory.CreateApiClient(), body ?? Body(factory, email));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        return await factory.GetUserAsync(email);
    }
}
