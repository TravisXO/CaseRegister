using CaseRegister.Domain.Accounts;
using FluentValidation;

namespace CaseRegister.Application.Accounts;

/// <param name="CertificateNumber">As printed on the certificate, for example 9611/2026.</param>
/// <param name="ValidFrom">1 January of the certificate's year.</param>
/// <param name="ValidTo">31 December of the certificate's year.</param>
public sealed record AddPractisingCertificateCommand(string CertificateNumber, DateOnly ValidFrom, DateOnly ValidTo);

public sealed class AddPractisingCertificateCommandValidator : AbstractValidator<AddPractisingCertificateCommand>
{
    public AddPractisingCertificateCommandValidator()
    {
        RuleFor(command => command.CertificateNumber).NotEmpty().MaximumLength(20);
        RuleFor(command => command.ValidFrom).NotEqual(default(DateOnly)).WithMessage("A valid-from date is required.");
        RuleFor(command => command.ValidTo).NotEqual(default(DateOnly)).WithMessage("A valid-to date is required.");
    }
}

public sealed record CertificateInfo(
    Guid Id,
    string Number,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    bool IsVerified,
    DateTimeOffset? VerifiedAt,
    bool IsCurrent);

public enum AddCertificateOutcome
{
    Added,

    /// <summary>The number or dates are not acceptable. The errors say why.</summary>
    Invalid,

    /// <summary>A certificate with that number already exists.</summary>
    Duplicate,

    /// <summary>The account is not a member (it has no membership number), so it has no certificates.</summary>
    NotAMember,
}

public sealed record AddCertificateResult(AddCertificateOutcome Outcome, Guid? CertificateId = null, IReadOnlyList<string>? Errors = null);

/// <summary>
/// Practising certificates: adding a new one, listing them, and asking whether a member has a current one.
/// Verifying one is not here: it grants access, so it goes through the two-admin approval process.
/// </summary>
public interface IPractisingCertificateService
{
    /// <summary>Adds a new certificate, unverified. Existing ones are never changed: this is a new row in the history.</summary>
    Task<AddCertificateResult> AddAsync(Guid userId, AddPractisingCertificateCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>A member's certificates, newest first.</summary>
    Task<IReadOnlyList<CertificateInfo>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True when the member holds a certificate that is verified and covers today.</summary>
    Task<bool> HasCurrentAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <param name="MembershipNumber">Shown as "&lt;prefix&gt; &lt;number&gt;". Null for association staff.</param>
public sealed record MemberProfile(
    Guid Id,
    string FullName,
    string Email,
    string? MembershipNumber,
    MembershipClass? MembershipClass,
    Guid? AgencyId,
    string? AgencyName,
    bool AgencyActive,
    IReadOnlyList<string> Roles,
    bool HasCurrentPractisingCertificate);

public interface IMemberProfileService
{
    Task<MemberProfile?> GetAsync(Guid userId, CancellationToken cancellationToken = default);
}
