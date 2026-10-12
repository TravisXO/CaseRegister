using CaseRegister.Domain.Accounts;
using FluentValidation;

namespace CaseRegister.Application.Applications;

/// <summary>
/// An agent applying to join. The account is created Pending, with nothing else: the membership number, agency
/// and certificate are claims for a reviewer to check, not facts.
/// </summary>
/// <param name="MembershipNumber">As the applicant types it, with or without the prefix.</param>
/// <param name="AgencyName">The agency as the applicant writes it. Reviewers match it to a real one.</param>
/// <param name="CertificateNumber">As printed on the certificate, for example 9611/2026.</param>
/// <param name="CertificateYear">The year the certificate is valid for. It has to match the number.</param>
/// <param name="TurnstileToken">From the Cloudflare Turnstile challenge on the sign-up page.</param>
public sealed record SignUpCommand(
    string FullName,
    string Email,
    string Password,
    string MembershipNumber,
    MembershipClass MembershipClass,
    string AgencyName,
    string CertificateNumber,
    int CertificateYear,
    string? TurnstileToken);

public sealed class SignUpCommandValidator : AbstractValidator<SignUpCommand>
{
    private const int MaxEmailLength = 254;
    private const int MaxNameLength = 200;
    private const int MaxMembershipNumberLength = 20;
    private const int MaxCertificateNumberLength = 20;
    private const int MaxTurnstileTokenLength = 2048;

    public SignUpCommandValidator()
    {
        RuleFor(command => command.FullName).NotEmpty().MaximumLength(MaxNameLength);
        RuleFor(command => command.Email).NotEmpty().EmailAddress().MaximumLength(MaxEmailLength);

        // The strength of the password is checked by the same rules as every account (length, breaches, your own details).
        // Here it only has to be present and not absurdly long, so it never reaches the slow hash unbounded.
        RuleFor(command => command.Password).NotEmpty().MaximumLength(Auth.PasswordPolicy.MaximumLength);
        RuleFor(command => command.MembershipNumber).NotEmpty().MaximumLength(MaxMembershipNumberLength);
        RuleFor(command => command.MembershipClass).IsInEnum();
        RuleFor(command => command.AgencyName).NotEmpty().MaximumLength(Domain.Applications.MemberApplication.MaxAgencyNameLength);
        RuleFor(command => command.CertificateNumber).NotEmpty().MaximumLength(MaxCertificateNumberLength);
        RuleFor(command => command.TurnstileToken).MaximumLength(MaxTurnstileTokenLength);
    }
}

public enum SignUpOutcome
{
    /// <summary>The same answer whether an application was made, an old one was reopened, or nothing was done at all.</summary>
    Submitted,

    /// <summary>Something about the details is wrong: shown to the applicant, and says nothing about any other account.</summary>
    Invalid,

    BotCheckFailed,
}

public sealed record SignUpResult(SignUpOutcome Outcome, IReadOnlyDictionary<string, string[]>? Errors = null);

public static class SignUpMessages
{
    /// <summary>The only thing a valid sign-up ever says. It does not reveal whether an account already existed.</summary>
    public const string Submitted = "If your details are valid, your application has been submitted.";
}

public interface IMemberSignUpService
{
    Task<SignUpResult> SignUpAsync(SignUpCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

public sealed record AgencyMatch(Guid Id, string Name);

/// <param name="Flags">Everything the reviewer must acknowledge to approve: those set at sign-up, and any that have arisen since.</param>
/// <param name="SuggestedAgencies">Existing agencies whose names are the same once case, punctuation and endings like Ltd are ignored. Only a suggestion.</param>
public sealed record ApplicationView(
    Guid ApplicationId,
    Guid UserId,
    string FullName,
    string Email,
    string MembershipNumber,
    MembershipClass MembershipClass,
    string AgencyName,
    string CertificateNumber,
    int CertificateYear,
    IReadOnlyList<string> Flags,
    DateTimeOffset SubmittedAt,
    int ReapplicationCount,
    IReadOnlyList<AgencyMatch> SuggestedAgencies);

public interface IApplicationReviewService
{
    /// <summary>Applications waiting for a decision, oldest first.</summary>
    Task<IReadOnlyList<ApplicationView>> ListPendingAsync(CancellationToken cancellationToken = default);
}
