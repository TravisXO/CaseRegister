using FluentValidation;

namespace CaseRegister.Application.Auth;

/// <summary>
/// Where passkeys are valid. A passkey belongs to one hostname (the relying party ID) and the browser will only offer it on that hostname,
/// which is what makes it phishing-resistant: a look-alike site is a different hostname. It is the hostname the browser sees, which is the
/// Cloudflare Pages hostname in production (never Render's, since the browser never visits Render) and "localhost" in development.
/// </summary>
public sealed class PasskeyOptions
{
    public const string SectionName = "Passkeys";
    public const string RelyingPartyIdKey = "Passkeys:RelyingPartyId";
    public const string OriginsKey = "Passkeys:Origins";

    /// <summary>The hostname only: no scheme, port or path. Example: <c>caseregister.pages.dev</c>.</summary>
    public string RelyingPartyId { get; set; } = string.Empty;

    /// <summary>
    /// The origins (scheme, host and port) a passkey ceremony may come from. Left empty it is <c>https://{RelyingPartyId}</c>.
    /// Development adds the Vite server (<c>http://localhost:5173</c>).
    /// </summary>
    public List<string> Origins { get; set; } = [];

    /// <summary>The origins that will be accepted: the configured ones, or the relying party's own https origin.</summary>
    public IReadOnlyList<string> EffectiveOrigins => Origins.Count > 0 ? [.. Origins.Select(origin => origin.Trim().TrimEnd('/'))] : [$"https://{RelyingPartyId}"];

    /// <summary>Null when the settings are usable, otherwise what is wrong (naming the setting, never a secret).</summary>
    public string? Problem(bool production)
    {
        var id = RelyingPartyId?.Trim() ?? string.Empty;
        if (id.Length == 0)
        {
            return $"{RelyingPartyIdKey} is not configured. Set it to the hostname the browser sees: the Cloudflare Pages hostname in production, 'localhost' in development.";
        }

        if (id.Any(char.IsUpper) || id.Contains("://", StringComparison.Ordinal) || id.Contains('/', StringComparison.Ordinal) || id.Contains(':', StringComparison.Ordinal))
        {
            return $"{RelyingPartyIdKey} must be a bare lower-case hostname (no scheme, port or path).";
        }

        if (production && (id == "localhost" || id.EndsWith(".onrender.com", StringComparison.Ordinal) || id.EndsWith(".localhost", StringComparison.Ordinal)))
        {
            return $"{RelyingPartyIdKey} must be the Cloudflare Pages hostname in production, not '{id}'. The browser never visits Render, and passkeys made for the wrong hostname would not work on the real site.";
        }

        foreach (var origin in EffectiveOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.PathAndQuery != "/" || uri.UserInfo.Length > 0)
            {
                return $"{OriginsKey} holds '{origin}', which is not an origin (scheme, host and optional port only).";
            }

            var local = uri.Host is "localhost";
            if (uri.Scheme != Uri.UriSchemeHttps && !(local && uri.Scheme == Uri.UriSchemeHttp))
            {
                return $"{OriginsKey} holds '{origin}': passkeys need https (http is only allowed for localhost).";
            }

            if (production && local)
            {
                return $"{OriginsKey} must not hold a localhost origin in production.";
            }

            if (uri.Host != id && !uri.Host.EndsWith("." + id, StringComparison.Ordinal))
            {
                return $"{OriginsKey} holds '{origin}', whose hostname is not '{id}' or a subdomain of it.";
            }
        }

        return null;
    }
}

/// <summary>How a person proves they are who they are for one sensitive act: an authenticator or recovery code, or a passkey assertion. Exactly one.</summary>
/// <param name="Code">A 6-digit authenticator code or a recovery code.</param>
/// <param name="PasskeyAssertion">The browser's answer to a passkey request (JSON), from the options the API gave for this purpose.</param>
public sealed record StepUp(string? Code, string? PasskeyAssertion)
{
    public bool IsValidShape => string.IsNullOrWhiteSpace(Code) != string.IsNullOrWhiteSpace(PasskeyAssertion);
}

/// <summary>Limits on what a step-up may carry; the passkey JSON is a few kilobytes at most.</summary>
public static class StepUpRules
{
    public const int MaxAssertionLength = 8192;

    /// <summary>Adds the rules for a command that carries an optional code and an optional passkey answer.</summary>
    public static void Require<T>(AbstractValidator<T> validator, Func<T, string?> code, Func<T, string?> assertion)
    {
        validator.RuleFor(command => command).Must(command => new StepUp(code(command), assertion(command)).IsValidShape)
            .WithMessage("Give either a code or a passkey answer, not both and not neither.");
        validator.RuleFor(command => code(command)).MaximumLength(TwoFactorCodeRules.MaxLength);
        validator.RuleFor(command => assertion(command)).MaximumLength(MaxAssertionLength);
    }
}

public sealed record PasskeyView(string Id, string Name, DateTimeOffset CreatedAt, bool BackedUp, IReadOnlyList<string> Transports);

/// <param name="OptionsJson">The WebAuthn creation or request options, as JSON the browser can pass on (parseCreationOptionsFromJSON).</param>
public sealed record PasskeyOptionsResult(string OptionsJson);

public enum PasskeyOutcome
{
    Succeeded,

    /// <summary>The ceremony did not check out: no user verification, a wrong origin, an unknown or replayed credential, or a stale challenge. They all look the same.</summary>
    Failed,

    /// <summary>The step-up (code or passkey) was not accepted.</summary>
    StepUpFailed,

    RateLimited,

    NotFound,

    /// <summary>The account cannot do this now (not approved, or a reset is waiting to be finished).</summary>
    NotAvailable,
}

public sealed record PasskeyResult(PasskeyOutcome Outcome, TimeSpan? RetryAfter = null, PasskeyView? Passkey = null);

/// <summary>Passkeys for the people who sign in: add, list, rename, remove, sign in with, and use as a step-up. TOTP stays as the fallback.</summary>
public interface IPasskeyService
{
    Task<IReadOnlyList<PasskeyView>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>The options for adding a passkey. The challenge is kept on the server side of the ceremony; the answer must come back with it.</summary>
    Task<PasskeyOptionsResult?> BeginRegistrationAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Adds the passkey the browser made, after a fresh step-up. A passkey that does not verify the user is refused.</summary>
    Task<PasskeyResult> CompleteRegistrationAsync(Guid userId, string credentialJson, string name, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default);

    Task<PasskeyResult> RenameAsync(Guid userId, string id, string name, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Removes one passkey, after a step-up. TOTP keeps working whatever is removed.</summary>
    Task<PasskeyResult> RemoveAsync(Guid userId, string id, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>The options for signing in with a passkey. Nobody is named: the passkey says who it belongs to.</summary>
    Task<PasskeyOptionsResult> BeginSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Signs in with a passkey that verified the user: password and second factor in one (amr "mfa").</summary>
    Task<PasskeyResult> CompleteSignInAsync(string credentialJson, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>The options for a step-up with one of this person's passkeys.</summary>
    Task<PasskeyOptionsResult?> BeginStepUpAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>Checks the second proof for a sensitive act: the one place that takes either an authenticator or recovery code or a passkey assertion.</summary>
public interface IStepUpVerifier
{
    Task<TwoFactorResult> VerifyAsync(Guid userId, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

// --- What the API takes -------------------------------------------------------------------------------------------------

/// <param name="Credential">What the browser made: the registration response, as JSON (<c>PublicKeyCredential.toJSON()</c>).</param>
/// <param name="Name">What the person calls it ("Work laptop"). Up to 60 characters.</param>
public sealed record AddPasskeyCommand(string Credential, string Name, string? TwoFactorCode = null, string? PasskeyAssertion = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public StepUp StepUp => new(TwoFactorCode, PasskeyAssertion);
}

public sealed record RenamePasskeyCommand(string Name);

public sealed record RemovePasskeyCommand(string? TwoFactorCode = null, string? PasskeyAssertion = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public StepUp StepUp => new(TwoFactorCode, PasskeyAssertion);
}

/// <param name="Credential">The sign-in response from the browser, as JSON (<c>PublicKeyCredential.toJSON()</c>).</param>
public sealed record PasskeySignInCommand(string Credential);

public sealed class AddPasskeyCommandValidator : AbstractValidator<AddPasskeyCommand>
{
    public AddPasskeyCommandValidator()
    {
        RuleFor(command => command.Credential).NotEmpty().MaximumLength(StepUpRules.MaxAssertionLength);
        RuleFor(command => command.Name).NotEmpty().MaximumLength(60);
        StepUpRules.Require(this, command => command.TwoFactorCode, command => command.PasskeyAssertion);
    }
}

public sealed class RenamePasskeyCommandValidator : AbstractValidator<RenamePasskeyCommand>
{
    public RenamePasskeyCommandValidator() => RuleFor(command => command.Name).NotEmpty().MaximumLength(60);
}

public sealed class RemovePasskeyCommandValidator : AbstractValidator<RemovePasskeyCommand>
{
    public RemovePasskeyCommandValidator() => StepUpRules.Require(this, command => command.TwoFactorCode, command => command.PasskeyAssertion);
}

public sealed class PasskeySignInCommandValidator : AbstractValidator<PasskeySignInCommand>
{
    public PasskeySignInCommandValidator() => RuleFor(command => command.Credential).NotEmpty().MaximumLength(StepUpRules.MaxAssertionLength);
}
