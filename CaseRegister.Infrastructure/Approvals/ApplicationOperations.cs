using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Invites;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Approvals;

/// <summary>The flags an application carries right now: those set when it was made, and any duplicate that has arisen since.</summary>
internal static class ApplicationFlagging
{
    public static async Task<ApplicationConcerns> EffectiveAsync(AppDbContext db, MemberApplication application, CancellationToken cancellationToken)
    {
        var flags = application.Flags;

        if (await IsNumberClaimedElsewhereAsync(db, application.MembershipNumber, application.UserId, cancellationToken))
        {
            flags |= ApplicationConcerns.DuplicateMembershipNumber;
        }

        return flags;
    }

    /// <summary>Another account holds the number, or another application that is still waiting claims it.</summary>
    public static async Task<bool> IsNumberClaimedElsewhereAsync(AppDbContext db, MembershipNumber number, Guid exceptUserId, CancellationToken cancellationToken) =>
        await db.Users.AnyAsync(user => user.Id != exceptUserId && user.MembershipNumber == number, cancellationToken)
        || await db.MemberApplications.AnyAsync(
            other => other.UserId != exceptUserId && other.MembershipNumber == number && other.Status == ApplicationStatus.Pending,
            cancellationToken);
}

/// <summary>
/// Approves an application, all of it or none of it: the account becomes Approved and gets its membership
/// number and class, it joins the chosen agency (an existing one, or a new one created now), its
/// certificate is recorded and verified, and it is made a registered agent under the role rules.
/// </summary>
internal sealed class ApproveApplicationOperation(AppDbContext db, UserManager<ApplicationUser> users, RoleAssignmentService roles, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.ApproveApplication;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var choice = ActionPayloads.ApproveApplication(context.Payload);
        var userId = context.TargetUserId!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var application = await db.MemberApplications.SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);

        if (user is null || application is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Pending || application.Status != ApplicationStatus.Pending)
        {
            return OperationProblems.WrongStatus("The application", "waiting");
        }

        List<ApprovalViolation> problems = [];

        // Which agency: an existing, active one, or a new one with a proper name.
        Agency? existing = null;
        if (choice.ExistingAgencyId is { } agencyId)
        {
            existing = await db.Agencies.SingleOrDefaultAsync(agency => agency.Id == agencyId, cancellationToken);
            if (existing is null)
            {
                problems.Add(new ApprovalViolation("AgencyNotFound", "The chosen agency does not exist."));
            }
            else if (existing.Status != AgencyStatus.Active)
            {
                problems.Add(new ApprovalViolation("AgencyNotActive", "The chosen agency is not active."));
            }
        }
        else if (string.IsNullOrWhiteSpace(choice.NewAgencyName) || choice.NewAgencyName.Trim().Length > MemberApplication.MaxAgencyNameLength)
        {
            problems.Add(new ApprovalViolation("AgencyNameInvalid", $"A new agency needs a name of 1 to {MemberApplication.MaxAgencyNameLength} characters."));
        }

        // Every flag has to be acknowledged in the proposal, so the confirmer sees it in what they approve.
        if (!ApplicationFlagNames.TryParse(choice.AcknowledgedFlags, out var acknowledged))
        {
            problems.Add(new ApprovalViolation("FlagUnknown", "The proposal names a flag that does not exist."));
        }

        var present = await ApplicationFlagging.EffectiveAsync(db, application, cancellationToken);
        var missing = present & ~acknowledged;
        if (missing != ApplicationConcerns.None)
        {
            problems.Add(new ApprovalViolation("FlagNotAcknowledged", $"This application is flagged and the proposal does not acknowledge: {string.Join(", ", ApplicationFlagNames.Of(missing))}."));
        }

        // The things the approval would create must still be free to create.
        if (await db.Users.AnyAsync(other => other.Id != userId && other.MembershipNumber == application.MembershipNumber, cancellationToken))
        {
            problems.Add(new ApprovalViolation("MembershipNumberTaken", "Another account already holds this membership number."));
        }

        if (application.CertificateNumber.Year < MembershipCalendar.Today(time).Year)
        {
            problems.Add(new ApprovalViolation("CertificateYearPassed", "The certificate's year has passed."));
        }

        if (await db.PractisingCertificates.AnyAsync(certificate => certificate.Number == application.CertificateNumber, cancellationToken))
        {
            problems.Add(new ApprovalViolation("CertificateNumberTaken", "That certificate number is already recorded."));
        }

        // The role rules, for what they would be afterwards: a registered agent of that agency.
        problems.AddRange(RoleAssignmentRules.Validate(new HashSet<Role> { Role.RegisteredAgent }, existing?.Id ?? Guid.NewGuid(), null).Select(violation => new ApprovalViolation(violation.Code, violation.Message)));

        if (problems.Count > 0)
        {
            return OperationPlan.Refuse([.. problems]);
        }

        var agencyDescription = existing is not null ? $"existing agency {existing.Name}" : $"new agency {choice.NewAgencyName!.Trim()}";

        return new OperationPlan(
            [],
            $"account: Pending; no agency; no roles; certificate {application.CertificateNumber}: unverified",
            $"account: Approved, {application.MembershipClass} member; {agencyDescription}; certificate {application.CertificateNumber}: verified; roles: RegisteredAgent",
            AuditAction.ApplicationApproved,
            userId.ToString(),
            async token =>
            {
                var agency = existing ?? Agency.Create(choice.NewAgencyName!);
                if (existing is null)
                {
                    db.Agencies.Add(agency);
                }

                user.ChangeStatus(AccountStatus.Approved, time.GetUtcNow());
                user.MembershipNumber = application.MembershipNumber;
                user.MembershipClass = application.MembershipClass;
                user.AgencyId = agency.Id;
                SessionEnding.Ensure(await users.UpdateAsync(user));

                var year = application.CertificateNumber.Year;
                var certificate = PractisingCertificate.Create(userId, application.CertificateNumber, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
                certificate.Verify(context.ActorId, time.GetUtcNow());
                db.PractisingCertificates.Add(certificate);

                application.Approve(context.ActorId, agency.Id, time.GetUtcNow());

                var rolePlan = await roles.PlanAsync(new RoleChange(userId, [Role.RegisteredAgent], [], ChangedByUserId: context.ActorId), token);
                if (rolePlan.Plan is null)
                {
                    throw new InvalidOperationException("The new agent could not be given the RegisteredAgent role.");
                }

                await roles.ApplyAsync(rolePlan.Plan, token);
                await db.SaveChangesAsync(token);
            });
    }
}

/// <summary>Turns an application down. The account is kept, marked Rejected, with the reason; the applicant may apply again.</summary>
internal sealed class RejectApplicationOperation(AppDbContext db, UserManager<ApplicationUser> users, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.RejectApplication;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var userId = context.TargetUserId!.Value;
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var application = await db.MemberApplications.SingleOrDefaultAsync(a => a.UserId == userId, cancellationToken);

        if (user is null || application is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Pending || application.Status != ApplicationStatus.Pending)
        {
            return OperationProblems.WrongStatus("The application", "waiting");
        }

        var reason = context.ProposalReason ?? string.Empty;

        return new OperationPlan(
            [],
            "account: Pending; application waiting",
            "account: Rejected (kept, never deleted); application rejected",
            AuditAction.ApplicationRejected,
            userId.ToString(),
            async token =>
            {
                user.ChangeStatus(AccountStatus.Rejected, time.GetUtcNow());
                SessionEnding.Ensure(await users.UpdateAsync(user));
                application.Reject(context.ActorId, reason, time.GetUtcNow());
                await db.SaveChangesAsync(token);
            });
    }
}

/// <summary>
/// Creates a staff account (an admin or a case reviewer) that cannot sign in yet, with its role, and a
/// one-time setup token. A newer invite for the same person replaces an unfinished one.
/// </summary>
internal sealed class InviteStaffOperation(AppDbContext db, UserManager<ApplicationUser> users, RoleAssignmentService roles, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.InviteStaff;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var invite = ActionPayloads.Invite(context.Payload);

        if (!ActionPayloads.InvitableRoles.Contains(invite.Role))
        {
            return OperationPlan.Refuse("InviteRoleNotAllowed", "Only an Admin or a CaseReviewer can be invited.");
        }

        var existing = await users.FindByEmailAsync(invite.Email);
        if (existing is not null && !await IsUnfinishedInviteAsync(existing, cancellationToken))
        {
            return OperationPlan.Refuse("EmailAlreadyExists", "An account with this email address already exists.");
        }

        var violations = RoleAssignmentRules.Validate(new HashSet<Role> { invite.Role }, null, null);
        if (violations.Count > 0)
        {
            return OperationPlan.Refuse([.. violations.Select(violation => new ApprovalViolation(violation.Code, violation.Message))]);
        }

        var replacing = existing is not null;

        return new OperationPlan(
            [],
            replacing ? "an unfinished invite for this address (its link stops working)" : "no account",
            $"account created, pending setup, with role {invite.Role}; setup link valid {StaffSetupToken.Lifetime.TotalHours:0} hours, single use",
            AuditAction.StaffInviteCreated,
            existing?.Id.ToString(),
            async token =>
            {
                var now = time.GetUtcNow();
                ApplicationUser user;

                if (existing is null)
                {
                    // Pending, with no password: it cannot sign in until setup is complete.
                    user = ApplicationUser.CreateStaff(invite.Email, invite.FullName, AccountStatus.Pending);
                    SessionEnding.Ensure(await users.CreateAsync(user));
                }
                else
                {
                    user = existing;
                    user.FullName = invite.FullName;
                    SessionEnding.Ensure(await users.UpdateAsync(user));

                    var stale = await db.StaffSetupTokens.Where(t => t.UserId == user.Id && t.UsedAt == null && t.RevokedAt == null).ToListAsync(token);
                    foreach (var old in stale)
                    {
                        old.Revoke(now);
                    }
                }

                var others = ActionPayloads.InvitableRoles.Where(role => role != invite.Role).ToList();
                var rolePlan = await roles.PlanAsync(new RoleChange(user.Id, [invite.Role], others, ChangedByUserId: context.ActorId), token);
                if (rolePlan.Result is { Succeeded: false })
                {
                    throw new InvalidOperationException("The invited person could not be given the role.");
                }

                if (rolePlan.Plan is not null)
                {
                    await roles.ApplyAsync(rolePlan.Plan, token);
                }

                // Only the hash is stored. The token itself goes back to the confirming admin once, and is kept nowhere.
                var raw = StaffSetupToken.NewToken();
                db.StaffSetupTokens.Add(StaffSetupToken.Issue(user.Id, StaffSetupToken.HashOf(raw), context.ActorId, now));
                await db.SaveChangesAsync(token);

                context.Output.SetupToken = raw;
            });
    }

    /// <summary>An invited account whose owner has not finished setup: no password yet, and a token that was never used.</summary>
    private async Task<bool> IsUnfinishedInviteAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        user is { AccountStatus: AccountStatus.Pending, PasswordHash: null, MembershipNumber: null }
        && await db.StaffSetupTokens.AnyAsync(token => token.UserId == user.Id && token.UsedAt == null, cancellationToken);
}
