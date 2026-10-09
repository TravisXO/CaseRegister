using CaseRegister.Domain.Common;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>One row of the audit table. Rows are only ever added: the application role cannot change or delete them.</summary>
public sealed class AuditLogRecord : Entity
{
    public string Action { get; init; } = string.Empty;

    public Guid? ActorUserId { get; init; }

    public string? Subject { get; init; }

    public string? IpAddress { get; init; }

    public string? Detail { get; init; }
}
