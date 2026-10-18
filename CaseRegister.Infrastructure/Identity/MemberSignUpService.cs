using CaseRegister.Application.Accounts;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Certificates;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>Runs every password rule (length, your own details, breaches) on a password without creating anything.</summary>
internal static class PasswordChecks
{
    public static async Task<IReadOnlyList<string>> ProblemsAsync(UserManager<ApplicationUser> users, ApplicationUser person, string password)
    {
        List<string> problems = [];

        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, person, password);
            problems.AddRange(result.Errors.Select(error => error.Description));
        }

        return problems;
    }
}

/// <summary>
/// Agent sign-up. Mistakes in the details are shown plainly. Whether an account already exists is never
/// shown: every valid sign-up gets the same answer, does the same slow work (one password hash, one breach
/// check) and takes about as long, whether it creates an application, reopens a rejected one, or does nothing.
/// </summary>
internal sealed class MemberSignUpService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IPasswordHasher<ApplicationUser> hasher,
    ITurnstileVerifier turnstile,
    IOptions<MembershipOptions> membership,
    NotificationDispatcher notifications,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<MemberSignUpService> logger,
    TimeProvider time) : IMemberSignUpService
{
    private sealed record Claims(
        string Email,
        string FullName,
        string Password,
        MembershipNumber Number,
        MembershipClass Class,
        string AgencyName,
        CertificateNumber Certificate);

    public async Task<SignUpResult> SignUpAsync(SignUpCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        // The bot check comes first, as at sign-in.
        if (!await turnstile.VerifyAsync(command.TurnstileToken, remoteIpAddress, cancellationToken))
        {
            return new SignUpResult(SignUpOutcome.BotCheckFailed);
        }

        var (claims, errors) = await CheckAsync(command);
        if (claims is null)
        {
            return new SignUpResult(SignUpOutcome.Invalid, errors);
        }

        var existing = await users.FindByEmailAsync(claims.Email);

        if (existing is null)
        {
            if (await TryCreateAsync(claims, remoteIpAddress, cancellationToken) is { } applicantId)
            {
                // Committed: the admins who review applications are told one has arrived (nothing about who).
                await notifications.ApplicationArrivedAsync(applicantId, time.GetUtcNow(), cancellationToken);
            }
            else
            {
                await IgnoreAsync(claims, "an account with this email address appeared at the same moment", remoteIpAddress, cancellationToken);
            }
        }
        else if (existing.AccountStatus == AccountStatus.Rejected
            && await db.MemberApplications.AnyAsync(application => application.UserId == existing.Id, cancellationToken))
        {
            await ReapplyAsync(existing, claims, remoteIpAddress, cancellationToken);
            await notifications.ApplicationArrivedAsync(existing.Id, time.GetUtcNow(), cancellationToken);
        }
        else
        {
            // Pending, Approved or Suspended (or staff): nothing is changed. The same slow work is still done.
            await IgnoreAsync(claims, $"an account with this email address already exists (status {existing.AccountStatus})", remoteIpAddress, cancellationToken);
        }

        return new SignUpResult(SignUpOutcome.Submitted);
    }

    /// <summary>The details the applicant can fix, shown as they are. None of this depends on any other account.</summary>
    private async Task<(Claims? Claims, IReadOnlyDictionary<string, string[]>? Errors)> CheckAsync(SignUpCommand command)
    {
        Dictionary<string, List<string>> errors = [];
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = [];
            }

            list.Add(message);
        }

        var email = EmailNormaliser.Normalise(command.Email);
        var fullName = command.FullName.Trim();
        var prefix = membership.Value.DisplayPrefix;

        if (!MembershipNumber.TryParse(command.MembershipNumber, prefix, out var number))
        {
            Add("MembershipNumber", $"Not a valid membership number. Use a form like {prefix} 884, {prefix}884, {prefix}-884 or 884.");
        }

        if (!CertificateNumber.TryParse(command.CertificateNumber, out var certificate))
        {
            Add("CertificateNumber", "Not a valid certificate number. Use a form like 9611/2026.");
        }
        else
        {
            if (command.CertificateYear != certificate.Year)
            {
                Add("CertificateYear", "The validity year must be the year in the certificate number.");
            }

            var accepted = ApplicationRules.AcceptedCertificateYears(MembershipCalendar.Today(time));
            if (!accepted.Contains(certificate.Year))
            {
                Add("CertificateNumber", $"The certificate must be for {string.Join(" or ", accepted)}.");
            }
        }

        // The same password rules as every account, run on every valid-looking sign-up whatever else is true.
        var person = number is null
            ? ApplicationUser.CreateStaff(email, fullName)
            : ApplicationUser.Create(email, fullName, number, AccountStatus.Pending, command.MembershipClass);
        foreach (var problem in await PasswordChecks.ProblemsAsync(users, person, command.Password))
        {
            Add("Password", problem);
        }

        if (errors.Count > 0)
        {
            return (null, errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        }

        return (new Claims(email, fullName, command.Password, number!, command.MembershipClass, command.AgencyName.Trim(), certificate!), null);
    }

    private async Task<Guid?> TryCreateAsync(Claims claims, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        Guid? created = null;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteVerifiedAsync(db, async () =>
            {
                created = null;
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                // Pending, with no membership number, agency, role or certificate. Those wait in the application.
                var user = ApplicationUser.CreateStaff(claims.Email, claims.FullName, AccountStatus.Pending);
                user.PasswordHash = hasher.HashPassword(user, claims.Password);

                if (!(await users.CreateAsync(user)).Succeeded)
                {
                    return;
                }

                var flags = await FlagsForAsync(claims.Number, user.Id, ApplicationConcerns.None, cancellationToken);
                db.MemberApplications.Add(MemberApplication.Submit(user.Id, claims.Number, claims.Class, claims.AgencyName, claims.Certificate, flags, time.GetUtcNow()));
                await db.SaveChangesAsync(cancellationToken);

                await LogAsync(AuditAction.MemberSignedUp, user.Id, flags, claims, remoteIpAddress, cancellationToken);
                created = user.Id;
                await transaction.CommitAsync(cancellationToken);
            });
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return null; // another sign-up with the same email won the race
        }

        return created;
    }

    private async Task ReapplyAsync(ApplicationUser user, Claims claims, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteVerifiedAsync(db, async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var tracked = await users.FindByIdAsync(user.Id.ToString());
            var application = await db.MemberApplications.SingleAsync(a => a.UserId == user.Id, cancellationToken);

            // Back to waiting, with the new details and a new password; the old decision stays in the audit log.
            tracked!.FullName = claims.FullName;
            tracked.PasswordHash = hasher.HashPassword(tracked, claims.Password);
            tracked.AccountStatus = AccountStatus.Pending;
            SessionEnding.Ensure(await users.UpdateAsync(tracked));
            SessionEnding.Ensure(await users.UpdateSecurityStampAsync(tracked));

            var flags = await FlagsForAsync(claims.Number, user.Id, ApplicationConcerns.Reapplication, cancellationToken);
            application.Resubmit(claims.Number, claims.Class, claims.AgencyName, claims.Certificate, flags, time.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);

            await LogAsync(AuditAction.MemberReapplied, user.Id, application.Flags, claims, remoteIpAddress, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>Nothing is saved, but the slow part is done anyway, so this takes as long as the paths that do save.</summary>
    private async Task IgnoreAsync(Claims claims, string detail, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        _ = hasher.HashPassword(new ApplicationUser(), claims.Password);

        await audit.WriteStandaloneAsync(
            new AuditEntry(
                AuditAction.MemberSignUpIgnored,
                ActorUserId: null,
                Subject: AuditSubject.ForAttemptedEmail(claims.Email),
                IpAddress: remoteIpAddress,
                Detail: detail),
            logger, cancellationToken);
    }

    private async Task<ApplicationConcerns> FlagsForAsync(MembershipNumber number, Guid userId, ApplicationConcerns start, CancellationToken cancellationToken) =>
        await ApplicationFlagging.IsNumberClaimedElsewhereAsync(db, number, userId, cancellationToken)
            ? start | ApplicationConcerns.DuplicateMembershipNumber
            : start;

    private async Task LogAsync(AuditAction action, Guid userId, ApplicationConcerns flags, Claims claims, string? remoteIpAddress, CancellationToken cancellationToken)
    {
        var names = ApplicationFlagNames.Of(flags);

        await audit.WriteAsync(
            new AuditEntry(
                action,
                userId,
                Subject: userId.ToString(),
                IpAddress: remoteIpAddress,
                Detail: $"certificate year {claims.Certificate.Year}; flags: {(names.Count == 0 ? "none" : string.Join(", ", names))}"),
            cancellationToken);

        foreach (var name in names)
        {
            await audit.WriteAsync(new AuditEntry(AuditAction.ApplicationFlagged, userId, Subject: userId.ToString(), IpAddress: remoteIpAddress, Detail: name), cancellationToken);
        }
    }
}

/// <summary>What reviewers see: the waiting applications, flagged, with suggested agencies.</summary>
internal sealed class ApplicationReviewService(AppDbContext db, UserManager<ApplicationUser> users, IOptions<MembershipOptions> membership) : IApplicationReviewService
{
    public async Task<IReadOnlyList<ApplicationView>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var waiting = await db.MemberApplications.AsNoTracking()
            .Where(application => application.Status == ApplicationStatus.Pending)
            .OrderBy(application => application.SubmittedAt)
            .ToListAsync(cancellationToken);

        var agencies = await db.Agencies.AsNoTracking().Where(agency => agency.Status == AgencyStatus.Active).ToListAsync(cancellationToken);
        List<ApplicationView> views = [];

        foreach (var application in waiting)
        {
            var user = await users.FindByIdAsync(application.UserId.ToString());
            if (user is null)
            {
                continue;
            }

            var flags = await ApplicationFlagging.EffectiveAsync(db, application, cancellationToken);

            // Only suggestions. Whether two names are one agency is for a reviewer to decide; nothing is merged here.
            var suggestions = agencies
                .Where(agency => AgencyNames.AreSame(agency.Name, application.AgencyName))
                .Select(agency => new AgencyMatch(agency.Id, agency.Name))
                .ToList();

            views.Add(new ApplicationView(
                application.Id,
                user.Id,
                user.FullName,
                user.Email!,
                application.MembershipNumber.Format(membership.Value.DisplayPrefix),
                application.MembershipClass,
                application.AgencyName,
                application.CertificateNumber.ToString(),
                application.CertificateNumber.Year,
                ApplicationFlagNames.Of(flags),
                application.SubmittedAt,
                application.ReapplicationCount,
                suggestions));
        }

        return views;
    }
}
