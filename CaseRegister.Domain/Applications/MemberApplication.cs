using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Applications;

/// <summary>Things a reviewer must know about an application, and explicitly acknowledge before approving it.</summary>
[Flags]
public enum ApplicationConcerns
{
    None = 0,

    /// <summary>The applicant was rejected before and has applied again.</summary>
    Reapplication = 1,

    /// <summary>Another account or another waiting application claims the same membership number.</summary>
    DuplicateMembershipNumber = 2,
}

public enum ApplicationStatus
{
    Pending,
    Approved,
    Rejected,
}

public static class ApplicationFlagNames
{
    /// <summary>The flags that are set, by name, in a fixed order. This is what a reviewer sees and what an approval has to list.</summary>
    public static IReadOnlyList<string> Of(ApplicationConcerns flags) =>
        [.. Enum.GetValues<ApplicationConcerns>().Where(flag => flag != ApplicationConcerns.None && flags.HasFlag(flag)).Select(flag => flag.ToString())];

    /// <summary>Reads names back. False if any name is not a flag.</summary>
    public static bool TryParse(IEnumerable<string> names, out ApplicationConcerns flags)
    {
        flags = ApplicationConcerns.None;

        foreach (var name in names)
        {
            if (!Enum.TryParse<ApplicationConcerns>(name, ignoreCase: false, out var flag) || flag == ApplicationConcerns.None || !Enum.IsDefined(flag))
            {
                return false;
            }

            flags |= flag;
        }

        return true;
    }
}

/// <summary>
/// What an applicant claimed when they signed up. The account itself stays Pending and has no membership
/// number, agency, role or certificate until two admins approve it: this is where the claims wait.
/// One row per applicant; applying again after a rejection updates the same row.
/// </summary>
public sealed class MemberApplication : Entity
{
    public const int MaxAgencyNameLength = 200;

    // For EF Core.
    private MemberApplication()
    {
    }

    public Guid UserId { get; private set; }

    public MembershipNumber MembershipNumber { get; private set; } = null!;

    public MembershipClass MembershipClass { get; private set; }

    /// <summary>The agency as the applicant wrote it. Reviewers decide which agency, existing or new, it really is.</summary>
    public string AgencyName { get; private set; } = string.Empty;

    /// <summary>The certificate claimed. Its year is the year it is valid for. It is unverified until approval.</summary>
    public CertificateNumber CertificateNumber { get; private set; } = null!;

    public ApplicationConcerns Flags { get; private set; }

    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Pending;

    public DateTimeOffset SubmittedAt { get; private set; }

    /// <summary>How many times this applicant has applied again after a rejection.</summary>
    public int ReapplicationCount { get; private set; }

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Why it was rejected.</summary>
    public string? DecisionReason { get; private set; }

    public Guid? ApprovedAgencyId { get; private set; }

    public static MemberApplication Submit(
        Guid userId,
        MembershipNumber membershipNumber,
        MembershipClass membershipClass,
        string agencyName,
        CertificateNumber certificateNumber,
        ApplicationConcerns flags,
        DateTimeOffset now)
    {
        var application = new MemberApplication { UserId = userId, SubmittedAt = now };
        application.SetClaims(membershipNumber, membershipClass, agencyName, certificateNumber, flags);

        return application;
    }

    /// <summary>A rejected applicant applies again with new details. The application goes back to waiting, flagged as a reapplication.</summary>
    public void Resubmit(
        MembershipNumber membershipNumber,
        MembershipClass membershipClass,
        string agencyName,
        CertificateNumber certificateNumber,
        ApplicationConcerns flags,
        DateTimeOffset now)
    {
        if (Status != ApplicationStatus.Rejected)
        {
            throw new InvalidOperationException("Only a rejected application can be submitted again.");
        }

        SetClaims(membershipNumber, membershipClass, agencyName, certificateNumber, flags | ApplicationConcerns.Reapplication);
        Status = ApplicationStatus.Pending;
        SubmittedAt = now;
        ReapplicationCount++;
        DecidedByUserId = null;
        DecidedAt = null;
        DecisionReason = null;
        ApprovedAgencyId = null;
    }

    public void Approve(Guid decidedByUserId, Guid agencyId, DateTimeOffset now)
    {
        RequirePending();
        Status = ApplicationStatus.Approved;
        DecidedByUserId = decidedByUserId;
        DecidedAt = now;
        ApprovedAgencyId = agencyId;
    }

    public void Reject(Guid decidedByUserId, string reason, DateTimeOffset now)
    {
        RequirePending();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = ApplicationStatus.Rejected;
        DecidedByUserId = decidedByUserId;
        DecidedAt = now;
        DecisionReason = reason.Trim();
    }

    private void RequirePending()
    {
        if (Status != ApplicationStatus.Pending)
        {
            throw new InvalidOperationException("Only a waiting application can be decided.");
        }
    }

    private void SetClaims(MembershipNumber membershipNumber, MembershipClass membershipClass, string agencyName, CertificateNumber certificateNumber, ApplicationConcerns flags)
    {
        ArgumentNullException.ThrowIfNull(membershipNumber);
        ArgumentNullException.ThrowIfNull(certificateNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(agencyName);

        var trimmed = agencyName.Trim();
        if (trimmed.Length > MaxAgencyNameLength)
        {
            throw new ArgumentException($"An agency name is at most {MaxAgencyNameLength} characters.", nameof(agencyName));
        }

        if (!Enum.IsDefined(membershipClass))
        {
            throw new ArgumentOutOfRangeException(nameof(membershipClass));
        }

        MembershipNumber = membershipNumber;
        MembershipClass = membershipClass;
        AgencyName = trimmed;
        CertificateNumber = certificateNumber;
        Flags = flags;
    }
}

/// <summary>Which practising certificate years an application may claim.</summary>
public static class ApplicationRules
{
    /// <summary>The current year; and during December, next year's too, because that is when the next one is issued.</summary>
    public static IReadOnlyList<int> AcceptedCertificateYears(DateOnly today) =>
        today.Month == 12 ? [today.Year, today.Year + 1] : [today.Year];
}
