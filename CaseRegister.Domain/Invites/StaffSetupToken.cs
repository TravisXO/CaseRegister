using System.Security.Cryptography;
using System.Text;
using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Invites;

/// <summary>
/// The secret in a staff invite link. Only its SHA-256 hash is stored, so a copy of the database cannot
/// be used to set up an account. It works once, within 24 hours, and a newer invite replaces it.
/// </summary>
public sealed class StaffSetupToken : Entity
{
    public const int TokenBytes = 32; // 256 bits
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    // For EF Core.
    private StaffSetupToken()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the token, lower-case hex.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Set when a newer invite for the same person replaced this one.</summary>
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The admin who confirmed the invite, and so saw the link.</summary>
    public Guid CreatedByUserId { get; private set; }

    public static StaffSetupToken Issue(Guid userId, string tokenHash, Guid createdByUserId, DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            TokenHash = tokenHash,
            CreatedByUserId = createdByUserId,
            ExpiresAt = now + Lifetime,
        };

    /// <summary>A new random token, as it is put in the link (URL-safe, no padding).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && RevokedAt is null && now < ExpiresAt;

    /// <summary>Why the token cannot be used, for the audit log (never shown to the caller).</summary>
    public string? Problem(DateTimeOffset now) =>
        UsedAt is not null ? "already used"
        : RevokedAt is not null ? "replaced by a newer invite"
        : now >= ExpiresAt ? "expired"
        : null;

    public void Spend(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new InvalidOperationException("This setup token cannot be used.");
        }

        UsedAt = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        if (UsedAt is null && RevokedAt is null)
        {
            RevokedAt = now;
        }
    }
}
