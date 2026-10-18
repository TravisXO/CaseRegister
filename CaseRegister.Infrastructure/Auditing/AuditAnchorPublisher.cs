using System.Globalization;
using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>
/// Stores an anchor of the audit chain outside the database, after a commit has asked for one (the first entry of a Zambian day, the first after
/// an hour, any confirmed proposal, a break-glass command, or a retry of a failed upload).
///
/// It runs in a scope and a database connection of its own, after the transaction that asked has committed, so nothing here can sit inside a unit
/// of work that is retried or rolled back. The upload is inline with a short timeout: no background task, because the host may be stopped at
/// any moment. If the anchor store cannot be reached the action that caused all this has already happened and is not touched: an
/// <see cref="AuditAction.AnchorDeferred"/> entry is written, the head is marked, and the next audit write tries again.
/// </summary>
internal sealed partial class AuditAnchorPublisher(
    AppDbContext db,
    AuditChainStore store,
    IAuditKeyring keys,
    IAuditAnchorSink sink,
    IAuditLog audit,
    TimeProvider time,
    ILogger<AuditAnchorPublisher> logger)
{
    /// <summary>How long the store has to take the anchor before it is given up on for now.</summary>
    public static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>An anchor is asked for by the first audit entry after this long since the last one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public async Task PublishAsync(string reason)
    {
        if (!sink.IsConfigured)
        {
            return;
        }

        try
        {
            var head = await store.ReadHeadAsync(CancellationToken.None);
            if (head.LastSequence == 0)
            {
                return;
            }

            var now = time.GetUtcNow();
            var anchor = AuditAnchoring.Make(keys.Current, head.LastSequence, head.LastHash, now);

            using var timeout = new CancellationTokenSource(UploadTimeout);
            try
            {
                await sink.WriteAsync(anchor, timeout.Token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                LogFailed(logger, head.LastSequence, reason, exception);
                await DeferAsync(head.LastSequence, alreadyDeferred: head.AnchorDeferred);

                return;
            }

            await db.AuditChainHeads.Where(row => row.Id == AuditChainHead.OnlyId).ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.LastAnchorDate, AuditChainFormat.ZambianDate(anchor.CreatedAt))
                    .SetProperty(row => row.LastAnchorAt, anchor.CreatedAt)
                    .SetProperty(row => row.AnchorDeferred, false));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An anchor never stops anything: whatever went wrong here, the action has already been done.
            LogUnexpected(logger, exception);
        }
    }

    private async Task DeferAsync(long sequence, bool alreadyDeferred)
    {
        // One entry when it first goes wrong; while the store stays down the next attempts are logged but do not each add an entry.
        if (!alreadyDeferred)
        {
            await audit.WriteAsync(new AuditEntry(
                AuditAction.AnchorDeferred,
                ActorUserId: null,
                Subject: sequence.ToString(CultureInfo.InvariantCulture),
                TargetType: "AuditLog",
                Detail: $"the anchor for sequence {sequence} could not be stored; it will be tried again with the next audit entry"));
        }

        await db.AuditChainHeads.Where(row => row.Id == AuditChainHead.OnlyId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.AnchorDeferred, true));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the audit anchor for sequence {Sequence} failed ({Reason}). It will be tried again with the next entry.")]
    private static partial void LogFailed(ILogger logger, long sequence, string reason, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Storing an audit anchor failed unexpectedly. The action that caused it was not affected.")]
    private static partial void LogUnexpected(ILogger logger, Exception exception);
}
