using System.Text.Json.Serialization;
using CaseRegister.Application.Auth;
using FluentValidation;

namespace CaseRegister.Application.Applications;

/// <summary>Where the invited person picks up the setup. Nothing is asked for beyond the token, the password and the code.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StaffSetupStartCommand(string Token);

/// <summary>
/// A password and two-factor, completed together. A request that says anything else (a role, a name,
/// a status) is not understood and is turned away: setup can do these two things and nothing more.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StaffSetupCompleteCommand(string Token, string Password, string TwoFactorCode);

public sealed class StaffSetupStartCommandValidator : AbstractValidator<StaffSetupStartCommand>
{
    public const int MaxTokenLength = 100;

    public StaffSetupStartCommandValidator() => RuleFor(command => command.Token).NotEmpty().MaximumLength(MaxTokenLength);
}

public sealed class StaffSetupCompleteCommandValidator : AbstractValidator<StaffSetupCompleteCommand>
{
    public StaffSetupCompleteCommandValidator()
    {
        RuleFor(command => command.Token).NotEmpty().MaximumLength(StaffSetupStartCommandValidator.MaxTokenLength);
        RuleFor(command => command.Password).NotEmpty().MaximumLength(PasswordPolicy.MaximumLength);
        RuleFor(command => command.TwoFactorCode).NotEmpty().MaximumLength(TwoFactorCodeRules.MaxLength);
    }
}

public enum StaffSetupOutcome
{
    Succeeded,

    /// <summary>Unknown, used, expired or replaced: they all look the same to the caller.</summary>
    InvalidToken,

    /// <summary>The password breaks the password rules. See the errors.</summary>
    InvalidPassword,

    /// <summary>The code does not match the key shown at the start.</summary>
    InvalidCode,

    /// <summary>The key has not been asked for yet.</summary>
    NoEnrolmentStarted,

    /// <summary>Too many failed attempts from this address.</summary>
    RateLimited,
}

public sealed record StaffSetupResult(
    StaffSetupOutcome Outcome,
    IReadOnlyList<string>? Errors = null,
    TwoFactorEnrolmentStart? Enrolment = null,
    IReadOnlyList<string>? RecoveryCodes = null);

public interface IStaffSetupService
{
    /// <summary>Makes the authenticator key for the invited person and returns it for the QR code. Nothing is switched on.</summary>
    Task<StaffSetupResult> StartAsync(string token, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the password and switches two-factor on, together or not at all. On success the account is
    /// Approved and the token is spent. Recovery codes are returned once.
    /// </summary>
    Task<StaffSetupResult> CompleteAsync(string token, string password, string twoFactorCode, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

/// <summary>Where the invited person finds the setup page. The link is this address, a "#", and the token.</summary>
public sealed class StaffInviteOptions
{
    public const string SetupPageUrlKey = "Invites:SetupPageUrl";

    public string SetupPageUrl { get; set; } = string.Empty;

    /// <summary>Absolute, and not carrying a fragment of its own: the token is the fragment.</summary>
    public static bool IsValid(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Fragment)
        && url is not null && !url.Contains('#', StringComparison.Ordinal)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    /// <summary>The token goes after a "#", which a browser never sends to a server, so it cannot appear in a server log.</summary>
    public string LinkFor(string token) => SetupPageUrl + "#" + token;
}
