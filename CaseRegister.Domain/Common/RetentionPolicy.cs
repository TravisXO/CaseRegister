using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;

namespace CaseRegister.Domain.Common;

/// <summary>
/// When a person's or an agency's records may be removed, decided in one place. Nothing is deleted anywhere yet (the integrity rules say nothing
/// is hard-deleted): this only records the earliest date a removal could be considered, in <c>retain_until</c>, so that when a removal process
/// is built it works from dates already on the records. A <c>legal_hold</c> stops any removal, whatever the date.
/// </summary>
/// <remarks>
/// The periods are the project's reading of what it must keep, and a lawyer should confirm them before real data is held:
/// the FIC's 2023 Estate Agents Guidelines ask for AML records to be kept at least 10 years after the relationship ends; an application that was
/// turned down never became a relationship, so its record is kept for a shorter, fixed time (long enough to answer an appeal or a complaint).
/// </remarks>
public static class RetentionPolicy
{
    /// <summary>AML records: at least this many years after the relationship ends.</summary>
    public const int RelationshipRecordYears = 10;

    /// <summary>A rejected application is kept this many months from the decision.</summary>
    public const int RejectedApplicationMonths = 12;

    /// <summary>
    /// The earliest date the record of an account may be removed, given the status it has just moved to and when. Null while the account is
    /// current or waiting: nothing is scheduled while a relationship is live.
    /// </summary>
    public static DateTimeOffset? RetainUntil(AccountStatus status, DateTimeOffset changedAt) => status switch
    {
        AccountStatus.Suspended => changedAt.AddYears(RelationshipRecordYears),
        AccountStatus.Rejected => changedAt.AddMonths(RejectedApplicationMonths),
        _ => null,
    };

    /// <summary>The same for an agency: a suspended agency's records are kept as a relationship's are; an active one has no date.</summary>
    public static DateTimeOffset? RetainUntil(AgencyStatus status, DateTimeOffset changedAt) => status switch
    {
        AgencyStatus.Suspended => changedAt.AddYears(RelationshipRecordYears),
        _ => null,
    };

    /// <summary>
    /// Whether a removal may be considered at all: there is a date, it has come, and nothing holds the record. This is not an instruction to
    /// remove; it is the question a removal process would have to ask first.
    /// </summary>
    public static bool MayBeConsideredForRemoval(DateTimeOffset? retainUntil, bool legalHold, DateTimeOffset now) =>
        !legalHold && retainUntil is { } until && until <= now;
}
