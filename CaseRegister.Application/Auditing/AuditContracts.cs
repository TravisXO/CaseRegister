using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Auditing;

/// <summary>
/// Where the audit keys and the anchors are configured. Keys come from user-secrets in Development and
/// environment variables elsewhere (Audit__Keys__k1), never from code.
/// </summary>
public sealed class AuditOptions
{
    public const string SectionName = "Audit";
    public const int MinimumKeyBytes = 32;

    /// <summary>The id of the key new entries are signed with. It is the only one that signs.</summary>
    public string CurrentKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Key id to key, as base64, at least 32 bytes each. Every key ever used stays here for ever: an entry is checked with the key named on it,
    /// so removing a retired key would make every entry it signed impossible to verify. Rotating adds a key and moves <see cref="CurrentKeyId"/>.
    /// </summary>
    public Dictionary<string, string> Keys { get; set; } = [];

    /// <summary>Where the local anchor file lives (relative to the content root unless absolute). Empty means no anchor store yet.</summary>
    public string? AnchorFilePath { get; set; }

    /// <summary>The Cloudflare R2 bucket the daily anchors are written to. Required in Production.</summary>
    public AuditR2Settings R2 { get; set; } = new();

    /// <summary>Why the settings are not usable, or null if they are.</summary>
    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(CurrentKeyId))
        {
            return "Audit:CurrentKeyId is not configured.";
        }

        if (!Keys.TryGetValue(CurrentKeyId, out var current) || string.IsNullOrWhiteSpace(current))
        {
            return $"Audit:Keys:{CurrentKeyId} is not configured. Set it with 'dotnet user-secrets' or the Audit__Keys__{CurrentKeyId} environment variable.";
        }

        // Every key that is configured must be usable, current or retired: a broken retired key would only be found when an old entry is checked.
        foreach (var (id, encoded) in Keys)
        {
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return $"Audit:Keys:{id} is empty.";
            }

            try
            {
                if (Convert.FromBase64String(encoded).Length < MinimumKeyBytes)
                {
                    return $"Audit:Keys:{id} must be at least {MinimumKeyBytes} bytes (base64).";
                }
            }
            catch (FormatException)
            {
                return $"Audit:Keys:{id} is not valid base64.";
            }
        }

        return null;
    }
}

/// <summary>
/// Where the anchors go in Cloudflare R2 (its S3 API). The credentials belong to a token that can reach this one bucket and
/// nothing else; they come from environment variables (Audit__R2__AccessKeyId and so on), never from code.
/// </summary>
public sealed class AuditR2Settings
{
    public const string SectionName = "Audit:R2";

    /// <summary>The account's R2 address, such as https://abc123.r2.cloudflarestorage.com (no bucket, no path).</summary>
    public string? Endpoint { get; set; }

    public string? Bucket { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    /// <summary>True when any of the four is set: a half-configured store is a mistake, not "no store".</summary>
    public bool AnySet => new[] { Endpoint, Bucket, AccessKeyId, SecretAccessKey }.Any(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Why the settings cannot be used (naming the setting, never its value), or null if they can.</summary>
    public string? Problem()
    {
        foreach (var (name, value) in new[] { ("Endpoint", Endpoint), ("Bucket", Bucket), ("AccessKeyId", AccessKeyId), ("SecretAccessKey", SecretAccessKey) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return $"{SectionName}:{name} is not configured. Set the Audit__R2__{name} environment variable.";
            }
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/" || endpoint.Query.Length > 0)
        {
            return $"{SectionName}:Endpoint must be an https address with no path, such as https://<account>.r2.cloudflarestorage.com.";
        }

        // R2 and S3 bucket names: 3 to 63 lower-case letters, digits and hyphens, not starting or ending with a hyphen.
        return Bucket!.Length is >= 3 and <= 63 && Bucket.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') && Bucket[0] != '-' && Bucket[^1] != '-'
            ? null
            : $"{SectionName}:Bucket must be 3 to 63 lower-case letters, digits or hyphens.";
    }
}

/// <summary>
/// The latest sequence number and hash, written somewhere an attacker with only database access cannot reach.
/// If the chain is later cut back or rewritten, the anchor no longer matches. The sink is replaceable: a local
/// file in Development, a Cloudflare R2 bucket in Production.
/// </summary>
/// <param name="ZambianDate">The Zambian date it was written for, such as 2026-10-12.</param>
/// <param name="Mac">HMAC over the other fields, so an edited anchor is noticed too.</param>
public sealed record AuditAnchor(long Sequence, string Hash, string KeyId, DateTimeOffset CreatedAt, string ZambianDate, string Mac);

public interface IAuditAnchorSink
{
    /// <summary>False when no store is configured: nothing can be written or checked.</summary>
    bool IsConfigured { get; }

    Task WriteAsync(AuditAnchor anchor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditAnchor>> ReadAllAsync(CancellationToken cancellationToken = default);
}

public enum AuditBreakKind
{
    /// <summary>The stored hash is not what the entry's fields give with the key: the row was edited, or the chain was rebuilt without the key.</summary>
    BadHash,

    /// <summary>The entry does not point at the hash of the entry before it: rows were reordered or swapped.</summary>
    BadPreviousHash,

    /// <summary>A sequence number is missing: a row was deleted (or never committed, which cannot happen).</summary>
    SequenceGap,

    /// <summary>The entry was signed with a key that is neither the current nor the previous one.</summary>
    UnknownKey,

    /// <summary>The chain head says something other than the last entry does: the end of the chain was cut off.</summary>
    HeadMismatch,

    /// <summary>An anchor does not match the entry at its sequence number, or the chain is shorter than the anchor says.</summary>
    AnchorMismatch,

    /// <summary>The anchor itself was edited: its signature is wrong.</summary>
    AnchorTampered,

    /// <summary>The anchor store could not be read.</summary>
    AnchorsUnreadable,
}

public sealed record AuditBreak(AuditBreakKind Kind, long? Sequence, string Message);

/// <param name="FromAnchorSequence">When checking started from an anchor: its sequence number. Everything up to it was trusted on the anchor's word.</param>
public sealed record AuditVerification(
    bool Intact,
    long EntriesChecked,
    long HeadSequence,
    int AnchorsChecked,
    bool AnchorStoreConfigured,
    long? FromAnchorSequence,
    AuditBreak? FirstBreak,
    DateTimeOffset VerifiedAt);

public interface IAuditVerifier
{
    /// <summary>
    /// Walks the chain, recomputing every hash and link, and checks the head and the anchors. Reports the first
    /// broken link. With <paramref name="fromLastGoodAnchor"/> it starts after the newest anchor that still matches.
    /// </summary>
    Task<AuditVerification> VerifyAsync(bool fromLastGoodAnchor, CancellationToken cancellationToken = default);

    /// <summary>Verifies, and records that it was done, and by whom. A verification is itself an audited act.</summary>
    Task<AuditVerification> VerifyAndRecordAsync(bool fromLastGoodAnchor, Guid? actorUserId, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

public interface IAuditAnchoring
{
    /// <summary>Writes an anchor for the chain as it stands. Null if there is nothing to anchor or no store is configured.</summary>
    Task<AuditAnchor?> AnchorNowAsync(Guid? actorUserId, CancellationToken cancellationToken = default);
}

/// <param name="Roles">The viewer's roles. Admins see everything; a compliance officer only their own agency.</param>
public sealed record AuditViewer(Guid UserId, IReadOnlyCollection<Role> Roles, Guid? AgencyId);

public sealed record AuditQuery(DateTimeOffset? From, DateTimeOffset? To, Guid? ActorUserId, string? Action, int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;
}

/// <param name="IpAddress">Null for anyone who is not an admin.</param>
public sealed record AuditEntryView(
    long Sequence,
    DateTimeOffset OccurredAt,
    string ActorType,
    Guid? ActorUserId,
    string? ActorRole,
    string Action,
    string? TargetType,
    string? TargetId,
    Guid? AgencyId,
    string? CorrelationId,
    string? IpAddress,
    string? Detail,
    string? Before,
    string? After);

public sealed record AuditPage(IReadOnlyList<AuditEntryView> Entries, int Page, int PageSize, long Total);

public enum AuditReadOutcome
{
    Ok,

    /// <summary>The viewer's roles give no access to the audit log.</summary>
    NotAllowed,
}

public sealed record AuditReadResult(AuditReadOutcome Outcome, AuditPage? Page = null);

public interface IAuditReader
{
    /// <summary>One page of entries the viewer is allowed to see. Each call writes its own entry to the log.</summary>
    Task<AuditReadResult> QueryAsync(AuditQuery query, AuditViewer viewer, string? remoteIpAddress, CancellationToken cancellationToken = default);
}
