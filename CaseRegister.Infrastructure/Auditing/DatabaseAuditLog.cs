using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Persistence;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>
/// A plain append-only table. This is the temporary implementation: it is replaced by the
/// hash-chained one, behind the same <see cref="IAuditLog"/> interface.
/// </summary>
internal sealed class DatabaseAuditLog(AppDbContext context) : IAuditLog
{
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        context.AuditLog.Add(new AuditLogRecord
        {
            Action = entry.Action.ToString(),
            ActorUserId = entry.ActorUserId,
            Subject = entry.Subject,
            IpAddress = entry.IpAddress,
            Detail = entry.Detail,
        });

        await context.SaveChangesAsync(cancellationToken);
    }
}
