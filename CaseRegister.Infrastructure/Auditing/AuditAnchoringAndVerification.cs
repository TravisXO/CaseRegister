using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>Anchors kept in a local file, one JSON object per line. For Development; a Cloudflare R2 sink replaces it later.</summary>
internal sealed class FileAuditAnchorSink(string path) : IAuditAnchorSink
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => true;

    public async Task WriteAsync(AuditAnchor anchor, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(anchor, _json) + "\n", Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AuditAnchor>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken);

            return [.. lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => JsonSerializer.Deserialize<AuditAnchor>(line, _json) ?? throw new InvalidDataException("An anchor line could not be read."))];
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>No anchor store is configured. Nothing is written, and verification says so rather than pretending anchors were checked.</summary>
internal sealed class NoAuditAnchorSink : IAuditAnchorSink
{
    public bool IsConfigured => false;

    public Task WriteAsync(AuditAnchor anchor, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditAnchor>> ReadAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditAnchor>>([]);
}

internal sealed partial class AuditAnchoring(AppDbContext db, AuditChainStore store, IAuditKeyring keys, IAuditAnchorSink sink, IAuditLog audit, TimeProvider time) : IAuditAnchoring
{
    public static AuditAnchor Make(AuditKey key, long sequence, string hash, DateTimeOffset now)
    {
        var createdAt = AuditChainFormat.FormatTime(now);
        var date = AuditChainFormat.ZambianDate(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new AuditAnchor(sequence, hash, key.Id, AuditChainFormat.TruncateToMicroseconds(now), date, AuditChainFormat.AnchorMac(key.Secret, sequence, hash, key.Id, createdAt, date));
    }

    public async Task<AuditAnchor?> AnchorNowAsync(Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        if (!sink.IsConfigured)
        {
            return null;
        }

        await store.EnsureImportedAsync(cancellationToken);
        var head = await store.ReadHeadAsync(cancellationToken);
        if (head.LastSequence == 0)
        {
            return null;
        }

        var anchor = Make(keys.Current, head.LastSequence, head.LastHash, time.GetUtcNow());
        await sink.WriteAsync(anchor, cancellationToken);

        // Done and written down: the date also stops the daily anchor repeating itself today.
        await store.InTransactionAsync(
            async () =>
            {
                await store.LockHeadAsync(cancellationToken);
                await db.AuditChainHeads.Where(row => row.Id == AuditChainHead.OnlyId)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.LastAnchorDate, AuditChainFormat.ZambianDate(anchor.CreatedAt)).SetProperty(row => row.LastAnchorAt, anchor.CreatedAt).SetProperty(row => row.AnchorDeferred, false), cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry(AuditAction.AuditAnchored, actorUserId, Subject: head.LastSequence.ToString(CultureInfo.InvariantCulture), TargetType: "AuditLog", Detail: $"anchored sequence {head.LastSequence}"),
                    cancellationToken);
            },
            cancellationToken);

        return anchor;
    }
}

/// <summary>Walks the chain and says where, if anywhere, it is broken.</summary>
internal sealed partial class AuditVerifier(
    AppDbContext db,
    AuditChainStore store,
    IAuditKeyring keys,
    IAuditAnchorSink sink,
    IAuditLog audit,
    TimeProvider time,
    ILogger<AuditVerifier> logger) : IAuditVerifier
{
    private const int BatchSize = 1000;

    public async Task<AuditVerification> VerifyAndRecordAsync(bool fromLastGoodAnchor, Guid? actorUserId, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var result = await VerifyAsync(fromLastGoodAnchor, cancellationToken);

        // The verification is itself an audited act, whatever it found.
        await audit.WriteAsync(
            new AuditEntry(
                AuditAction.AuditVerified,
                actorUserId,
                Subject: "chain",
                IpAddress: remoteIpAddress,
                TargetType: "AuditLog",
                Detail: result.Intact
                    ? $"intact: {result.EntriesChecked} entries checked up to {result.HeadSequence}{(result.FromAnchorSequence is { } from ? $", from anchor {from}" : string.Empty)}"
                    : $"BROKEN: {result.FirstBreak!.Kind} at {result.FirstBreak.Sequence}",
                After: new { intact = result.Intact, entriesChecked = result.EntriesChecked, firstBreak = result.FirstBreak?.Kind.ToString(), at = result.FirstBreak?.Sequence }),
            cancellationToken);

        return result;
    }

    public async Task<AuditVerification> VerifyAsync(bool fromLastGoodAnchor, CancellationToken cancellationToken = default)
    {
        await store.EnsureImportedAsync(cancellationToken);
        var head = await store.ReadHeadAsync(cancellationToken);
        var breaks = new List<AuditBreak>();

        // --- The anchors: which ones are genuine, and which still match the chain -----------------------------
        IReadOnlyList<AuditAnchor> anchors = [];
        try
        {
            anchors = await sink.ReadAllAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            LogAnchorsUnreadable(logger, exception);
            breaks.Add(new AuditBreak(AuditBreakKind.AnchorsUnreadable, null, "The anchor store could not be read."));
        }

        AuditAnchor? startAnchor = null;
        var checkedAnchors = 0;
        foreach (var anchor in anchors.OrderByDescending(a => a.Sequence))
        {
            checkedAnchors++;
            var mismatch = await AnchorProblemAsync(anchor, cancellationToken);
            if (mismatch is not null)
            {
                breaks.Add(mismatch);
            }
            else if (startAnchor is null)
            {
                startAnchor = anchor; // the newest one that is genuine and still matches
            }
        }

        // --- The chain ---------------------------------------------------------------------------------
        var from = fromLastGoodAnchor && startAnchor is not null ? startAnchor.Sequence : 0;
        var expectedPrevious = from == 0 ? AuditChainFormat.GenesisHash : startAnchor!.Hash;
        var expected = from + 1;
        long checkedEntries = 0;
        var lastSeen = from;
        var lastHash = expectedPrevious;
        AuditBreak? entryBreak = null;

        while (entryBreak is null)
        {
            var low = expected;
            var batch = await db.AuditLog.AsNoTracking().Where(record => record.Sequence >= low).OrderBy(record => record.Sequence).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var record in batch)
            {
                entryBreak = CheckEntry(record, expected, expectedPrevious);
                if (entryBreak is not null)
                {
                    break;
                }

                checkedEntries++;
                lastSeen = record.Sequence;
                lastHash = record.EntryHash;
                expectedPrevious = record.EntryHash;
                expected = record.Sequence + 1;
            }
        }

        if (entryBreak is not null)
        {
            breaks.Add(entryBreak);
        }
        else if (lastSeen != head.LastSequence || !string.Equals(lastHash, head.LastHash, StringComparison.Ordinal) && lastSeen > 0)
        {
            // The entries end somewhere other than where the head says they do: the tail was cut off (or the head was edited).
            breaks.Add(new AuditBreak(AuditBreakKind.HeadMismatch, Math.Min(lastSeen, head.LastSequence) + 1, $"The chain ends at {lastSeen} but the head says {head.LastSequence}."));
        }

        var first = breaks.OrderBy(b => b.Sequence ?? long.MaxValue).ThenBy(b => b.Kind).FirstOrDefault();

        return new AuditVerification(
            first is null,
            checkedEntries,
            head.LastSequence,
            checkedAnchors,
            sink.IsConfigured,
            from == 0 ? null : from,
            first,
            time.GetUtcNow());
    }

    /// <summary>Whether this entry is what the chain says it should be. Null if it is.</summary>
    private AuditBreak? CheckEntry(AuditLogRecord record, long expectedSequence, string expectedPrevious)
    {
        if (record.Sequence != expectedSequence)
        {
            return new AuditBreak(AuditBreakKind.SequenceGap, expectedSequence, $"Sequence {expectedSequence} is missing (the next entry is {record.Sequence}).");
        }

        if (!string.Equals(record.PreviousHash, expectedPrevious, StringComparison.Ordinal))
        {
            return new AuditBreak(AuditBreakKind.BadPreviousHash, record.Sequence, $"Entry {record.Sequence} does not follow on from the entry before it.");
        }

        if (!keys.TryGet(record.KeyId, out var key))
        {
            return new AuditBreak(AuditBreakKind.UnknownKey, record.Sequence, $"Entry {record.Sequence} was signed with key '{record.KeyId}', which is not the current or previous key.");
        }

        var fields = new AuditFields(
            record.Sequence,
            AuditChainFormat.FormatTime(record.OccurredAt),
            record.ActorType,
            record.ActorUserId?.ToString("D"),
            record.ActorRole,
            record.Action,
            record.TargetType,
            record.Subject,
            record.AgencyId?.ToString("D"),
            record.CorrelationId,
            record.IpAddress,
            record.Detail,
            record.BeforeJson,
            record.AfterJson,
            record.PreviousHash,
            record.KeyId);

        var recomputed = AuditChainFormat.Hash(key.Secret, fields);

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(recomputed), Encoding.ASCII.GetBytes(record.EntryHash))
            ? null
            : new AuditBreak(AuditBreakKind.BadHash, record.Sequence, $"Entry {record.Sequence} does not match its hash: it was changed, or the chain was rebuilt without the key.");
    }

    private async Task<AuditBreak?> AnchorProblemAsync(AuditAnchor anchor, CancellationToken cancellationToken)
    {
        if (!keys.TryGet(anchor.KeyId, out var key)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(AuditChainFormat.AnchorMac(key.Secret, anchor.Sequence, anchor.Hash, anchor.KeyId, AuditChainFormat.FormatTime(anchor.CreatedAt), anchor.ZambianDate)),
                Encoding.ASCII.GetBytes(anchor.Mac)))
        {
            return new AuditBreak(AuditBreakKind.AnchorTampered, anchor.Sequence, $"The anchor for sequence {anchor.Sequence} has been altered.");
        }

        var stored = await db.AuditLog.AsNoTracking().Where(record => record.Sequence == anchor.Sequence).Select(record => record.EntryHash).SingleOrDefaultAsync(cancellationToken);

        return stored is null
            ? new AuditBreak(AuditBreakKind.AnchorMismatch, anchor.Sequence, $"The anchor says the chain reached {anchor.Sequence}, but there is no such entry.")
            : string.Equals(stored, anchor.Hash, StringComparison.Ordinal)
                ? null
                : new AuditBreak(AuditBreakKind.AnchorMismatch, anchor.Sequence, $"Entry {anchor.Sequence} does not match the anchor taken of it.");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit anchors could not be read.")]
    private static partial void LogAnchorsUnreadable(ILogger logger, Exception exception);
}

/// <summary>Reads the log for the people allowed to, and writes down that they did.</summary>
internal sealed class AuditReader(AppDbContext db, AuditChainStore store, IAuditLog audit, IAuditReadLog readLog) : IAuditReader
{
    public async Task<AuditReadResult> QueryAsync(AuditQuery query, AuditViewer viewer, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var isAdmin = viewer.Roles.Contains(Role.Admin);
        var isOfficer = viewer.Roles.Contains(Role.AgencyComplianceOfficer);

        // An admin sees everything. A compliance officer sees only their own agency's entries, and has to have an agency.
        if (!isAdmin && !(isOfficer && viewer.AgencyId is not null))
        {
            return new AuditReadResult(AuditReadOutcome.NotAllowed);
        }

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, AuditQuery.MaxPageSize);

        var entries = db.AuditLog.AsNoTracking().AsQueryable();
        if (!isAdmin)
        {
            var agency = viewer.AgencyId;
            entries = entries.Where(record => record.AgencyId == agency);
        }

        if (query.From is { } from)
        {
            entries = entries.Where(record => record.OccurredAt >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(record => record.OccurredAt <= to);
        }

        if (query.ActorUserId is { } actor)
        {
            entries = entries.Where(record => record.ActorUserId == actor);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            var action = query.Action.Trim();
            entries = entries.Where(record => record.Action == action);
        }

        AuditPage result = null!;

        // The query and the entry that records it are one unit: no read without its entry.
        await store.InTransactionAsync(
            async () =>
            {
                var total = await entries.LongCountAsync(cancellationToken);
                var rows = await entries.OrderByDescending(record => record.Sequence).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);

                result = new AuditPage(
                    [.. rows.Select(record => new AuditEntryView(
                        record.Sequence,
                        record.OccurredAt,
                        record.ActorType,
                        record.ActorUserId,
                        record.ActorRole,
                        record.Action,
                        record.TargetType,
                        record.Subject,
                        record.AgencyId,
                        record.CorrelationId,
                        isAdmin ? record.IpAddress : null, // only admins see where people connected from
                        record.Detail,
                        record.BeforeJson,
                        record.AfterJson))],
                    page,
                    size,
                    total);

                // One entry for each person and filter in any ten minutes (paging through the results is the same view), not one for every request.
                var filter = $"{(isAdmin ? "all" : viewer.AgencyId?.ToString("D"))}|{query.From:O}|{query.To:O}|{query.ActorUserId}|{query.Action}";
                if (!await readLog.ClaimAsync(viewer.UserId, "audit-log", filter, cancellationToken))
                {
                    return;
                }

                await audit.WriteAsync(
                    new AuditEntry(
                        AuditAction.AuditLogRead,
                        viewer.UserId,
                        Subject: isAdmin ? "all" : viewer.AgencyId?.ToString("D"),
                        IpAddress: remoteIpAddress,
                        TargetType: "AuditLog",
                        Detail: $"scope {(isAdmin ? "all" : "own agency")}; from {query.From:O}; to {query.To:O}; actor {query.ActorUserId}; action {query.Action}; page {page} size {size}; {rows.Count} of {total}"),
                    cancellationToken);
            },
            cancellationToken);

        return new AuditReadResult(AuditReadOutcome.Ok, result);
    }
}
