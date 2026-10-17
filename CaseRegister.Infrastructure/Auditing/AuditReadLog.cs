using System.Security.Cryptography;
using System.Text;
using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Auditing;

internal sealed class AuditReadLog(AppDbContext db, IAuditLog audit, TimeProvider time) : IAuditReadLog
{
    public async Task<bool> ClaimAsync(Guid userId, string view, string filter, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var bucket = now.ToUnixTimeSeconds() / (long)IAuditReadLog.Window.TotalSeconds;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}|{view}|{filter}|{bucket}")));

        var written = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO audit_read_markers (key, created_at) VALUES ({key}, {now}) ON CONFLICT (key) DO NOTHING",
            cancellationToken);

        if (written == 1)
        {
            // Housekeeping with no scheduler: markers from before yesterday can never match a new key again.
            var old = now - TimeSpan.FromDays(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM audit_read_markers WHERE created_at < {old}", cancellationToken);
        }

        return written == 1;
    }

    public Task NoteViewAsync(Guid userId, string view, string filter, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        Transactions.RunAsync(
            db,
            async () =>
            {
                if (await ClaimAsync(userId, view, filter, cancellationToken))
                {
                    await audit.WriteAsync(
                        new AuditEntry(AuditAction.ListViewed, userId, Subject: view, IpAddress: remoteIpAddress, TargetType: "AdminList", Detail: AuditText.Fit($"viewed the {view} list; filter: {(filter.Length == 0 ? "none" : filter)}")),
                        cancellationToken);
                }
            },
            cancellationToken);
}
