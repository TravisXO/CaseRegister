using System.Text.Json;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// The WebAuthn ceremonies, one place for all three uses (adding a passkey, signing in, a step-up). Identity's own handler does the checking
/// (challenge, origin, signature, counter, and user verification); this keeps the challenge on the server's side: the options go to the browser, and
/// the state that holds the challenge goes into a short-lived, encrypted, HttpOnly cookie that only this service can read. The browser's answer is
/// checked against it. The challenge therefore can never be chosen by the client, and a ceremony cannot outlive its five minutes.
/// </summary>
internal sealed class PasskeyCeremonies(
    UserManager<ApplicationUser> users,
    IPasskeyHandler<ApplicationUser> handler,
    IHttpContextAccessor accessor,
    IDataProtectionProvider protection,
    TimeProvider time)
{
    public const string CookieName = "__Host-cr-passkey";
    public const string Purpose = "CaseRegister.PasskeyCeremony.v1";

    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("A passkey ceremony needs a request.");

    private sealed record Envelope(string Kind, Guid? UserId, string State, DateTimeOffset ExpiresAt);

    private const string Create = "create";
    private const string Request = "request";

    // --- Adding a passkey ---------------------------------------------------------------------------------------------

    public async Task<string?> StartCreationAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var entity = new PasskeyUserEntity { Id = user.Id.ToString(), Name = user.Email ?? user.UserName ?? user.Id.ToString(), DisplayName = string.IsNullOrWhiteSpace(user.FullName) ? (user.Email ?? "Account") : user.FullName };
        var result = await handler.MakeCreationOptionsAsync(entity, Context);
        Remember(Create, user.Id, result.AttestationState!);

        return result.CreationOptionsJson;
    }

    /// <summary>The passkey the browser made, if it checks out and verified the user. Null for anything else.</summary>
    public async Task<UserPasskeyInfo?> FinishCreationAsync(Guid userId, string credentialJson, CancellationToken cancellationToken = default)
    {
        var state = Take(Create, userId);
        if (state is null)
        {
            return null;
        }

        PasskeyAttestationResult result;
        try
        {
            result = await handler.PerformAttestationAsync(new PasskeyAttestationContext { HttpContext = Context, CredentialJson = credentialJson, AttestationState = state });
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or PasskeyException)
        {
            return null;
        }

        // Identity refuses a passkey that did not verify the user when its options asked for verification; this does not rely on that alone.
        if (!result.Succeeded || result.Passkey is null || !result.Passkey.IsUserVerified || result.UserEntity?.Id != userId.ToString())
        {
            return null;
        }

        return result.Passkey;
    }

    // --- Signing in, or a step-up --------------------------------------------------------------------------------------

    /// <summary>The options for an assertion. With a user, only that person's passkeys are offered; without one, the passkey says whose it is.</summary>
    public async Task<string> StartRequestAsync(ApplicationUser? user, CancellationToken cancellationToken = default)
    {
        var result = await handler.MakeRequestOptionsAsync(user!, Context);
        Remember(Request, user?.Id, result.AssertionState!);

        return result.RequestOptionsJson;
    }

    /// <summary>
    /// The person and passkey an assertion proves, if it checks out and verified the user (and, when <paramref name="expectedUserId"/> is given,
    /// belongs to that person). The passkey's counter is saved. Null for anything else.
    /// </summary>
    public async Task<(ApplicationUser User, UserPasskeyInfo Passkey)?> FinishRequestAsync(string credentialJson, Guid? expectedUserId, CancellationToken cancellationToken = default)
    {
        var state = Take(Request, expectedUserId);
        if (state is null)
        {
            return null;
        }

        PasskeyAssertionResult<ApplicationUser> result;
        try
        {
            result = await handler.PerformAssertionAsync(new PasskeyAssertionContext { HttpContext = Context, CredentialJson = credentialJson, AssertionState = state });
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or PasskeyException)
        {
            return null;
        }

        if (!result.Succeeded || result.User is null || result.Passkey is null || !result.Passkey.IsUserVerified)
        {
            return null;
        }

        if (expectedUserId is { } expected && result.User.Id != expected)
        {
            return null;
        }

        // The counter (and backup state) moves on; a replay of this assertion no longer verifies.
        var saved = await users.AddOrUpdatePasskeyAsync(result.User, result.Passkey);

        return saved.Succeeded ? (result.User, result.Passkey) : null;
    }

    // --- The state cookie -----------------------------------------------------------------------------------------------

    private void Remember(string kind, Guid? userId, string state)
    {
        var envelope = new Envelope(kind, userId, state, time.GetUtcNow() + _lifetime);
        Context.Response.Cookies.Append(CookieName, _protector.Protect(JsonSerializer.Serialize(envelope)), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = _lifetime,
            IsEssential = true,
        });
    }

    /// <summary>Reads and removes the ceremony's state. It works once, for the kind and person it was made for, within its lifetime.</summary>
    private string? Take(string kind, Guid? userId)
    {
        var raw = Context.Request.Cookies[CookieName];
        Context.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(_protector.Unprotect(raw));

            return envelope is not null && envelope.Kind == kind && envelope.UserId == userId && envelope.ExpiresAt > time.GetUtcNow() ? envelope.State : null;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
