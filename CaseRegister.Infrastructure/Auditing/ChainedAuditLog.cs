using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>The keys the chain is signed with, from configuration. Startup fails if they are missing or too short.</summary>
internal sealed class AuditKeyring : IAuditKeyring
{
    private readonly Dictionary<string, AuditKey> _accepted = new(StringComparer.Ordinal);

    public AuditKeyring(IOptions<AuditOptions> options)
    {
        var settings = options.Value;
        if (settings.Problem() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        Current = new AuditKey(settings.CurrentKeyId, Convert.FromBase64String(settings.Keys[settings.CurrentKeyId]));
        _accepted[Current.Id] = Current;

        if (!string.IsNullOrWhiteSpace(settings.PreviousKeyId))
        {
            _accepted[settings.PreviousKeyId] = new AuditKey(settings.PreviousKeyId, Convert.FromBase64String(settings.Keys[settings.PreviousKeyId]));
        }
    }

    public AuditKey Current { get; }

    public IReadOnlyList<AuditKey> Accepted => [.. _accepted.Values];

    public bool TryGet(string keyId, out AuditKey key) => _accepted.TryGetValue(keyId, out key!);
}

/// <summary>What kind of thing an entry is about, when the caller did not say.</summary>
internal static class AuditTargets
{
    public const string None = "None";

    public static string TypeFor(AuditEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.TargetType))
        {
            return entry.TargetType;
        }

        if (string.IsNullOrWhiteSpace(entry.Subject))
        {
            return None;
        }

        if (entry.Subject.StartsWith("email-sha256:", StringComparison.Ordinal))
        {
            return "Email";
        }

        if (Guid.TryParse(entry.Subject, out _))
        {
            return entry.Action switch
            {
                AuditAction.AgencySuspended or AuditAction.AgencyReactivated => "Agency",
                AuditAction.SessionRevoked => "Session",
                _ => "User",
            };
        }

        return entry.Action is AuditAction.PractisingCertificateAdded or AuditAction.PractisingCertificateVerified ? "PractisingCertificate" : "Other";
    }
}

/// <summary>A row of the table the chain replaced, read once to bring it into the chain.</summary>
internal sealed class LegacyAuditRow
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid? ActorUserId { get; set; }

    public string? Subject { get; set; }

    public string? IpAddress { get; set; }

    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The mechanics of the chain: lock the head, bring in the old rows, build an entry with its hash. Everything
/// that appends goes through here, so there is one way an entry is made.
/// </summary>
internal sealed partial class AuditChainStore(
    AppDbContext db,
    IAuditKeyring keys,
    IHttpContextAccessor http,
    ILogger<AuditChainStore> logger)
{
    private const int MaxDetail = 500;

    /// <summary>Takes the lock on the chain head for the rest of the transaction. Only one appender holds it at a time.</summary>
    public async Task<AuditChainHead> LockHeadAsync(CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The chain head can only be locked inside a transaction.");
        }

        // Read fresh and untracked, so a head left behind by an earlier rolled-back transaction is never used.
        var rows = await db.AuditChainHeads
            .FromSql($"SELECT id, last_sequence, last_hash, last_anchor_date, legacy_imported FROM audit_chain_head WHERE id = 1 FOR UPDATE")
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows.Single();
    }

    /// <summary>The head as last committed, without locking it.</summary>
    public async Task<AuditChainHead> ReadHeadAsync(CancellationToken cancellationToken) =>
        await db.AuditChainHeads.AsNoTracking().SingleAsync(head => head.Id == AuditChainHead.OnlyId, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> inside the caller's transaction, or in one of its own. Either way the chain head
    /// is locked first, so appends are one at a time.
    /// </summary>
    public Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) => Transactions.RunAsync(db, work, cancellationToken);

    /// <summary>
    /// Brings the rows that were written before the chain existed into it, in order, as its start. Done once, with
    /// the head locked. Returns the sequence number and hash the chain now ends at.
    /// </summary>
    public async Task<(long Sequence, string Hash)> ImportLegacyAsync(long sequence, string lastHash, CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQuery<LegacyAuditRow>($"SELECT id, action, actor_user_id, subject, ip_address, detail, created_at FROM audit_log_legacy ORDER BY created_at, id")
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            _ = Enum.TryParse<AuditAction>(row.Action, out var action);
            var entry = new AuditEntry(action, row.ActorUserId, row.Subject, row.IpAddress, row.Detail);
            var record = Build(
                ++sequence,
                lastHash,
                AuditChainFormat.TruncateToMicroseconds(row.CreatedAt),
                entry with { Detail = row.Detail },
                row.Action,
                actorRole: row.ActorUserId is null ? "system" : "unknown",
                agencyId: null,
                correlationId: null);
            db.AuditLog.Add(record);
            lastHash = record.EntryHash;
        }

        await db.SaveChangesAsync(cancellationToken);
        await db.AuditChainHeads.Where(head => head.Id == AuditChainHead.OnlyId).ExecuteUpdateAsync(
            set => set
                .SetProperty(head => head.LegacyImported, true)
                .SetProperty(head => head.LastSequence, sequence)
                .SetProperty(head => head.LastHash, lastHash),
            cancellationToken);

        LogImported(logger, rows.Count);

        return (sequence, lastHash);
    }

    /// <summary>Makes sure the old rows are in the chain. Does nothing once they are.</summary>
    public Task EnsureImportedAsync(CancellationToken cancellationToken) =>
        InTransactionAsync(
            async () =>
            {
                var head = await LockHeadAsync(cancellationToken);
                if (!head.LegacyImported)
                {
                    await ImportLegacyAsync(head.LastSequence, head.LastHash, cancellationToken);
                }
            },
            cancellationToken);

    /// <summary>Everything about the entry that is worked out from the world rather than given: who, which agency, which request.</summary>
    public async Task<AuditLogRecord> BuildForAppendAsync(long sequence, string previousHash, AuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var role = "system";
        Guid? actorAgency = null;

        if (entry.ActorUserId is { } actorId)
        {
            var names = await db.UserRoles.AsNoTracking()
                .Where(userRole => userRole.UserId == actorId)
                .Join(db.Roles, userRole => userRole.RoleId, identityRole => identityRole.Id, (_, identityRole) => identityRole.Name)
                .ToListAsync(cancellationToken);
            var actor = await db.Users.AsNoTracking().Where(user => user.Id == actorId).Select(user => new { user.AgencyId }).SingleOrDefaultAsync(cancellationToken);

            role = actor is null ? "unknown" : names.Count == 0 ? "none" : string.Join(',', names.Order(StringComparer.Ordinal));
            actorAgency = actor?.AgencyId;
        }

        var type = AuditTargets.TypeFor(entry);
        Guid? agencyId = null;
        if (Guid.TryParse(entry.Subject, out var subjectId))
        {
            agencyId = type switch
            {
                "Agency" => subjectId,
                "User" => await db.Users.AsNoTracking().Where(user => user.Id == subjectId).Select(user => user.AgencyId).SingleOrDefaultAsync(cancellationToken),
                _ => null,
            };
        }

        var context = http.HttpContext;

        return Build(
            sequence,
            previousHash,
            now,
            entry with { IpAddress = entry.IpAddress ?? context?.Connection.RemoteIpAddress?.ToString() },
            entry.Action.ToString(),
            role,
            agencyId ?? actorAgency,
            Truncate(context?.TraceIdentifier, 100),
            type);
    }

    private AuditLogRecord Build(
        long sequence,
        string previousHash,
        DateTimeOffset occurredAt,
        AuditEntry entry,
        string action,
        string actorRole,
        Guid? agencyId,
        string? correlationId,
        string? targetType = null)
    {
        var key = keys.Current;
        var type = targetType ?? AuditTargets.TypeFor(entry);
        var before = AuditRedactor.Prepare(entry.Before);
        var after = AuditRedactor.Prepare(entry.After);
        var detail = Truncate(entry.Detail, MaxDetail);
        var actorType = entry.ActorUserId is null ? "system" : "user";

        var fields = new AuditFields(
            sequence,
            AuditChainFormat.FormatTime(occurredAt),
            actorType,
            entry.ActorUserId?.ToString("D"),
            actorRole,
            action,
            type,
            entry.Subject,
            agencyId?.ToString("D"),
            correlationId,
            entry.IpAddress,
            detail,
            before,
            after,
            previousHash,
            key.Id);

        return new AuditLogRecord
        {
            Sequence = sequence,
            OccurredAt = AuditChainFormat.TruncateToMicroseconds(occurredAt),
            ActorType = actorType,
            ActorUserId = entry.ActorUserId,
            ActorRole = actorRole,
            Action = action,
            TargetType = type,
            Subject = entry.Subject,
            AgencyId = agencyId,
            CorrelationId = correlationId,
            IpAddress = entry.IpAddress,
            Detail = detail,
            BeforeJson = before,
            AfterJson = after,
            PreviousHash = previousHash,
            EntryHash = AuditChainFormat.Hash(key.Secret, fields),
            KeyId = key.Id,
        };
    }

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];

    [LoggerMessage(Level = LogLevel.Information, Message = "Brought {Count} earlier audit rows into the hash chain.")]
    private static partial void LogImported(ILogger logger, int count);
}

/// <summary>
/// The audit log: each entry is a link in a keyed hash chain. It is written in the caller's database transaction
/// when there is one, so there is never an action without its entry or an entry for an action that rolled back; if
/// the write fails the exception goes to the caller and the action fails with it.
/// </summary>
internal sealed partial class ChainedAuditLog(
    AppDbContext db,
    AuditChainStore store,
    IAuditKeyring keys,
    IAuditAnchorSink anchors,
    TimeProvider time,
    ILogger<ChainedAuditLog> logger) : IAuditLog
{
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        store.InTransactionAsync(() => AppendAsync(entry, cancellationToken), cancellationToken);

    private async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var head = await store.LockHeadAsync(cancellationToken);
            var sequence = head.LastSequence;
            var lastHash = head.LastHash;
            var importedNow = false;

            if (!head.LegacyImported)
            {
                (sequence, lastHash) = await store.ImportLegacyAsync(sequence, lastHash, cancellationToken);
                importedNow = true;
            }

            var now = AuditChainFormat.TruncateToMicroseconds(time.GetUtcNow());

            // The first entry of a new Zambian day anchors the chain as it stood (all of it committed). Not on the
            // call that imported the old rows: if that call's transaction rolled back, the anchor would point at rows that are gone.
            var anchoredOn = head.LastAnchorDate;
            if (!importedNow && sequence > 0 && anchors.IsConfigured && AuditChainFormat.ZambianDate(now) is var today && (anchoredOn is null || today > anchoredOn)
                && await TryAnchorAsync(sequence, lastHash, now, cancellationToken))
            {
                anchoredOn = today;
            }

            var record = await store.BuildForAppendAsync(sequence + 1, lastHash, entry, now, cancellationToken);
            db.AuditLog.Add(record);
            await db.SaveChangesAsync(cancellationToken);

            await db.AuditChainHeads.Where(row => row.Id == AuditChainHead.OnlyId).ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.LastSequence, record.Sequence)
                    .SetProperty(row => row.LastHash, record.EntryHash)
                    .SetProperty(row => row.LastAnchorDate, anchoredOn),
                cancellationToken);
        }
        catch
        {
            // Nothing half-written is left in the context to be saved by accident later.
            foreach (var added in db.ChangeTracker.Entries<AuditLogRecord>().Where(tracked => tracked.State == EntityState.Added).ToList())
            {
                added.State = EntityState.Detached;
            }

            throw;
        }
    }

    /// <summary>Writes an anchor. A store that fails is logged and tried again at the next entry; it never stops an action.</summary>
    private async Task<bool> TryAnchorAsync(long sequence, string hash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await anchors.WriteAsync(AuditAnchoring.Make(keys.Current, sequence, hash, now), cancellationToken);

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogAnchorFailed(logger, sequence, exception);

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the audit anchor for sequence {Sequence} failed. It will be tried again with the next entry.")]
    private static partial void LogAnchorFailed(ILogger logger, long sequence, Exception exception);
}

/// <summary>For events that stand alone and must not break their response (a failed sign-in): the failure is logged instead of thrown.</summary>
internal static partial class AuditExtensions
{
    public static async Task WriteStandaloneAsync(this IAuditLog audit, AuditEntry entry, ILogger logger, CancellationToken cancellationToken = default)
    {
        try
        {
            await audit.WriteAsync(entry, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStandaloneFailed(logger, entry.Action, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the audit entry for {Action} failed. The response was not changed.")]
    private static partial void LogStandaloneFailed(ILogger logger, AuditAction action, Exception exception);
}
