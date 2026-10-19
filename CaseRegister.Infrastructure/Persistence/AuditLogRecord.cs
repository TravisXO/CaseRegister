using CaseRegister.Domain.Common;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// One link of the audit chain. Rows are only ever added: the application role cannot change or delete them, and
/// a trigger refuses it for everyone. The hash covers every field below except the hash itself, and the previous
/// entry's hash, so changing, removing or reordering a row breaks the chain.
/// </summary>
public sealed class AuditLogRecord : Entity
{
    /// <summary>1, 2, 3 ... with no gaps. Taken from the locked chain head, not from a database sequence.</summary>
    public long Sequence { get; init; }

    /// <summary>When it happened, UTC, to the microsecond.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>"user" or "system".</summary>
    public string ActorType { get; init; } = "system";

    public Guid? ActorUserId { get; init; }

    public string? ActorRole { get; init; }

    public string Action { get; init; } = string.Empty;

    public string? TargetType { get; init; }

    /// <summary>The id of what was acted on.</summary>
    public string? Subject { get; init; }

    /// <summary>The agency the entry belongs to, for the compliance officers who may read their own agency's entries.</summary>
    public Guid? AgencyId { get; init; }

    /// <summary>The trace id of the request that did it.</summary>
    public string? CorrelationId { get; init; }

    public string? IpAddress { get; init; }

    public string? Detail { get; init; }

    /// <summary>Canonical JSON, secrets stripped, size capped.</summary>
    public string? BeforeJson { get; init; }

    public string? AfterJson { get; init; }

    public string PreviousHash { get; init; } = string.Empty;

    public string EntryHash { get; init; } = string.Empty;

    public string KeyId { get; init; } = string.Empty;
}

/// <summary>
/// The one row that says where the chain ends. Appending locks it (SELECT ... FOR UPDATE) for the rest of the
/// transaction, so appends happen one at a time, the chain cannot fork, and sequence numbers have no gaps.
/// </summary>
public sealed class AuditChainHead
{
    public const int OnlyId = 1;

    public int Id { get; init; } = OnlyId;

    public long LastSequence { get; set; }

    public string LastHash { get; set; } = string.Empty;

    /// <summary>The Zambian date of the last anchor written.</summary>
    public DateOnly? LastAnchorDate { get; set; }

    /// <summary>When the last anchor was stored. An hour after this, the next audit entry asks for another.</summary>
    public DateTimeOffset? LastAnchorAt { get; set; }

    /// <summary>The last anchor could not be stored (the anchor store was down). The next audit write tries again.</summary>
    public bool AnchorDeferred { get; set; }

    /// <summary>Whether the rows from before the chain existed have been brought into it.</summary>
    public bool LegacyImported { get; set; }
}
