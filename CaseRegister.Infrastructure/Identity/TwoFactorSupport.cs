using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>Encrypts and decrypts authenticator keys for storage, with a purpose of their own.</summary>
public sealed class AuthenticatorKeyProtector(IDataProtectionProvider provider)
{
    // A different purpose from every other use of the key ring, so a value protected for one thing
    // cannot be fed to another.
    public const string Purpose = "CaseRegister.AuthenticatorKey.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(byte[] key) => _protector.Protect(Convert.ToBase64String(key));

    public byte[] Unprotect(string protectedKey) => Convert.FromBase64String(_protector.Unprotect(protectedKey));
}

/// <summary>The two-factor attempt limit. The default is the real one; tests raise it.</summary>
public sealed class TwoFactorOptions
{
    public const string SectionName = "TwoFactor";

    /// <summary>Codes checked per user per minute, right or wrong.</summary>
    public int AttemptsPerMinutePerUser { get; set; } = 5;
}

/// <summary>
/// Limits how often one user's two-factor codes can be tried: 5 a minute, whoever is trying and from
/// wherever. A 6-digit code is guessable by brute force if nothing slows the guesses down.
/// </summary>
public sealed class TwoFactorAttemptLimiter : IDisposable
{
    private const int Segments = 6;
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public TwoFactorAttemptLimiter(IOptions<TwoFactorOptions> options)
    {
        var permitLimit = options.Value.AttemptsPerMinutePerUser;

        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetSlidingWindowLimiter(userId, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = Segments,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public bool TryAcquire(Guid userId, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);

        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>
/// Checks a password and, when it is wrong, counts the failure against the account and the address it came from (see
/// <see cref="SignInThrottle"/>). A right password does NOT reset the counters: that happens only once the second factor has passed too,
/// so someone with a stolen password cannot keep clearing them while guessing codes.
/// </summary>
internal sealed class CredentialChecker(
    Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> users,
    SignInThrottle throttle)
{
    public async Task<bool> VerifyPasswordAsync(ApplicationUser user, string password, string? ipAddress, CancellationToken cancellationToken)
    {
        if (await users.CheckPasswordAsync(user, password))
        {
            return true;
        }

        await throttle.RecordPasswordFailureAsync(user, ipAddress, cancellationToken);

        return false;
    }
}