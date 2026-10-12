using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CaseRegister.Application.Auditing;

/// <summary>Everything about an entry that its hash covers. The same fields, read back from the database, give the same hash.</summary>
/// <param name="OccurredAt">Already formatted by <see cref="AuditChainFormat.FormatTime"/>: UTC, to the microsecond.</param>
/// <param name="Before">Canonical JSON text, or null.</param>
public sealed record AuditFields(
    long Sequence,
    string OccurredAt,
    string ActorType,
    string? ActorId,
    string? ActorRole,
    string Action,
    string? TargetType,
    string? TargetId,
    string? AgencyId,
    string? CorrelationId,
    string? IpAddress,
    string? Detail,
    string? Before,
    string? After,
    string PreviousHash,
    string KeyId);

/// <summary>A key the chain is signed with. Entries record the id of the key that signed them.</summary>
public sealed record AuditKey(string Id, byte[] Secret);

public interface IAuditKeyring
{
    /// <summary>The key new entries are signed with.</summary>
    AuditKey Current { get; }

    /// <summary>The current key, and the previous one if there is one: the keys an entry may legitimately carry.</summary>
    IReadOnlyList<AuditKey> Accepted { get; }

    bool TryGet(string keyId, out AuditKey key);
}

/// <summary>
/// How an audit entry becomes a hash. The entry is written as canonical JSON (RFC 8785), previous hash included,
/// and signed with HMAC-SHA256. A key is needed to make a valid hash, so someone who can edit the database but
/// does not hold the key cannot rewrite the chain.
/// </summary>
public static class AuditChainFormat
{
    /// <summary>The "previous hash" of the very first entry.</summary>
    public static readonly string GenesisHash = new('0', 64);

    public const int TicksPerMicrosecond = 10;

    /// <summary>Postgres keeps microseconds. Truncating before hashing means a time read back hashes the same as the one written.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();

        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }

    public static string FormatTime(DateTimeOffset time) =>
        TruncateToMicroseconds(time).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    public static string Canonical(AuditFields fields)
    {
        var entry = new JsonObject
        {
            ["sequence"] = fields.Sequence,
            ["occurredAt"] = fields.OccurredAt,
            ["actorType"] = fields.ActorType,
            ["actorId"] = fields.ActorId,
            ["actorRole"] = fields.ActorRole,
            ["action"] = fields.Action,
            ["targetType"] = fields.TargetType,
            ["targetId"] = fields.TargetId,
            ["agencyId"] = fields.AgencyId,
            ["correlationId"] = fields.CorrelationId,
            ["ipAddress"] = fields.IpAddress,
            ["detail"] = fields.Detail,
            ["before"] = fields.Before is null ? null : JsonNode.Parse(fields.Before),
            ["after"] = fields.After is null ? null : JsonNode.Parse(fields.After),
            ["previousHash"] = fields.PreviousHash,
            ["keyId"] = fields.KeyId,
        };

        return Jcs.Canonicalise(entry);
    }

    /// <summary>The entry hash: lower-case hex of HMAC-SHA256 over the canonical entry, previous hash included.</summary>
    public static string Hash(byte[] key, AuditFields fields) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(fields))));

    /// <summary>Signs an anchor, so an anchor edited outside the database is noticed too.</summary>
    public static string AnchorMac(byte[] key, long sequence, string hash, string keyId, string createdAt, string zambianDate)
    {
        var anchor = new JsonObject
        {
            ["sequence"] = sequence,
            ["hash"] = hash,
            ["keyId"] = keyId,
            ["createdAt"] = createdAt,
            ["zambianDate"] = zambianDate,
        };

        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Jcs.CanonicaliseToUtf8(anchor)));
    }

    /// <summary>The date in Zambia (UTC+2, no daylight saving): when a "new day" starts for anchoring.</summary>
    public static DateOnly ZambianDate(DateTimeOffset time) => DateOnly.FromDateTime(time.ToUniversalTime().AddHours(2).DateTime);
}
