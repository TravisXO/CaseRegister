using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Certificates;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class PractisingCertificateService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IAuditLog audit,
    TimeProvider timeProvider) : IPractisingCertificateService
{
    public async Task<AddCertificateResult> AddAsync(
        Guid userId,
        AddPractisingCertificateCommand command,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());

        // Only members have certificates. Association staff are not members.
        if (user?.MembershipNumber is null)
        {
            return new AddCertificateResult(AddCertificateOutcome.NotAMember);
        }

        if (!CertificateNumber.TryParse(command.CertificateNumber, out var number))
        {
            return new AddCertificateResult(AddCertificateOutcome.Invalid, Errors: ["The certificate number must look like 9611/2026: up to six digits, a slash and a four-digit year."]);
        }

        var problems = PractisingCertificate.Check(number, command.ValidFrom, command.ValidTo);
        if (problems.Count > 0)
        {
            return new AddCertificateResult(AddCertificateOutcome.Invalid, Errors: problems);
        }

        if (await db.PractisingCertificates.AnyAsync(c => c.Number == number, cancellationToken))
        {
            return new AddCertificateResult(AddCertificateOutcome.Duplicate);
        }

        // A new row every time. Nothing here ever changes an existing certificate: that is the history.
        var certificate = PractisingCertificate.Create(userId, number, command.ValidFrom, command.ValidTo);
        db.PractisingCertificates.Add(certificate);

        try
        {
            // The certificate and the entry about adding it are one transaction.
            await CaseRegister.Infrastructure.Approvals.Transactions.RunAsync(
                db,
                async () =>
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.WriteAsync(
                        new AuditEntry(
                            AuditAction.PractisingCertificateAdded,
                            userId,
                            Subject: number.ToString(),
                            IpAddress: remoteIpAddress,
                            Detail: $"valid {command.ValidFrom:yyyy-MM-dd} to {command.ValidTo:yyyy-MM-dd}",
                            TargetType: "PractisingCertificate"),
                        cancellationToken);
                },
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();

            return new AddCertificateResult(AddCertificateOutcome.Duplicate); // someone added it at the same moment
        }


        return new AddCertificateResult(AddCertificateOutcome.Added, certificate.Id);
    }

    public async Task<IReadOnlyList<CertificateInfo>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var today = MembershipCalendar.Today(timeProvider);

        var certificates = await db.PractisingCertificates
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.ValidFrom)
            .ToListAsync(cancellationToken);

        return [.. certificates.Select(c => new CertificateInfo(c.Id, c.Number.ToString(), c.ValidFrom, c.ValidTo, c.IsVerified, c.VerifiedAt, c.IsCurrent(today)))];
    }

    public Task<bool> HasCurrentAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var today = MembershipCalendar.Today(timeProvider);

        return db.PractisingCertificates
            .Where(c => c.UserId == userId)
            .Where(PractisingCertificate.CurrentOn(today))
            .AnyAsync(cancellationToken);
    }
}

internal sealed class MemberProfileService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IPractisingCertificateService certificates,
    IOptions<MembershipOptions> membership) : IMemberProfileService
{
    public async Task<MemberProfile?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return null;
        }

        var agencyName = user.AgencyId is { } agencyId
            ? await db.Agencies.AsNoTracking().Where(a => a.Id == agencyId).Select(a => a.Name).SingleOrDefaultAsync(cancellationToken)
            : null;

        return new MemberProfile(
            user.Id,
            user.FullName,
            user.Email!,
            user.MembershipNumber?.Format(membership.Value.DisplayPrefix),
            user.MembershipClass,
            user.AgencyId,
            agencyName,
            [.. (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal)],
            await certificates.HasCurrentAsync(userId, cancellationToken));
    }
}
