namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// "The view of this list by this person with this filter was already written down in this ten minutes." Its unique key is what makes
/// INSERT ... ON CONFLICT DO NOTHING choose who writes the audit entry. Rows older than a day are cleared as new ones are made.
/// </summary>
public sealed class AuditReadMarker
{
    public string Key { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; }
}
