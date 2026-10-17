using CaseRegister.Domain.Common;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// A user's authenticator (TOTP) key. The key is stored encrypted, never in the clear. It only counts
/// once <see cref="EnabledAt"/> is set, which happens after a valid code proves the app has it.
/// </summary>
public sealed class AuthenticatorRecord : Entity
{
    public AuthenticatorRecord()
    {
    }

    public AuthenticatorRecord(Guid userId, string encryptedKey)
    {
        UserId = userId;
        EncryptedKey = encryptedKey;
    }

    public Guid UserId { get; private set; }

    /// <summary>The key, encrypted with the data protection key ring (itself encrypted by the certificate).</summary>
    public string EncryptedKey { get; set; } = string.Empty;

    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>
    /// Set when an admin reset this account's two-factor. The row is kept (nothing is deleted) but no
    /// longer counts: every query skips it, and the person enrols again with a new key.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>The time step of the last code accepted. A code from that step or an earlier one is a replay.</summary>
    public long? LastUsedStep { get; set; }
}

/// <summary>A recovery code. Only its hash is stored; it works once and is then retired.</summary>
public sealed class RecoveryCodeRecord : Entity
{
    public RecoveryCodeRecord()
    {
    }

    public RecoveryCodeRecord(Guid userId, string codeHash)
    {
        UserId = userId;
        CodeHash = codeHash;
    }

    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>Set when a new set of codes replaced this one before it was used.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// The one-time code made when an admin's reset (or the command line) removes a person's two-factor. Only its hash is stored. The person
/// needs it, with their password, before they can set two-factor up again. It can be typed in again and again within 72 hours until the person has
/// finished enrolling (<see cref="CompletedAt"/>, which is also when <see cref="UsedAt"/> is set): someone who abandons set-up can come back. Until
/// then a plain password sign-in never gets in, so a half-finished enrolment does not leave the account open to anyone who has the password.
/// </summary>
public sealed class EnrolmentCodeRecord : Entity
{
    public EnrolmentCodeRecord()
    {
    }

    public EnrolmentCodeRecord(Guid userId, string codeHash, DateTimeOffset expiresAt)
    {
        UserId = userId;
        CodeHash = codeHash;
        ExpiresAt = expiresAt;
    }

    public Guid UserId { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the code was used up: the moment enrolment was finished with it. Not set by merely typing the code in.</summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>When the person finished setting two-factor up. Until then the account has to go through the code.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Set when a newer code replaced this one.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Still standing in the way of a plain password sign-in: not finished and not replaced.</summary>
    public bool IsOutstanding => CompletedAt is null && RevokedAt is null;
}

/// <summary>
/// A sign-in session kept on the server. The browser's cookie holds only a session id; everything
/// else lives here, so a session can be ended at once by removing its row.
/// </summary>
public sealed class UserSession : Entity
{
    public UserSession()
    {
    }

    public UserSession(
        Guid userId,
        string sessionHash,
        byte[] ticket,
        DateTimeOffset expiresAt,
        DateTimeOffset lastActiveAt,
        string? ipAddress,
        string? userAgent)
    {
        UserId = userId;
        SessionHash = sessionHash;
        Ticket = ticket;
        ExpiresAt = expiresAt;
        LastActiveAt = lastActiveAt;
        IpAddress = ipAddress;
        UserAgent = userAgent;
    }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the session id in the cookie. The id itself is not stored, so a copy of this table cannot be used to sign in.</summary>
    public string SessionHash { get; private set; } = string.Empty;

    /// <summary>The authentication ticket (who, claims, timings), serialised and protected.</summary>
    public byte[] Ticket { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset LastActiveAt { get; set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }
}

/// <summary>
/// One successful full sign-in (password and second factor), as a place: country and city only, never an address and never coordinates.
/// It is what "Last signed in" and "a new location" are worked out from. Kept, not deleted.
/// </summary>
public sealed class SignInHistoryEntry : Entity
{
    public SignInHistoryEntry()
    {
    }

    public SignInHistoryEntry(Guid userId, string? country, string? city)
    {
        UserId = userId;
        Country = country;
        City = city;
    }

    public Guid UserId { get; private set; }

    public string? Country { get; private set; }

    public string? City { get; private set; }
}
