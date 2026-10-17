namespace CaseRegister.Application.Auditing;

/// <summary>
/// Writes down that an admin looked at a list (applications, proposals, admins, people), once for each person, list and filter in any
/// ten minutes, so scrolling and reloading make one entry and not hundreds. There is no timer: a marker row with a unique key says "already
/// written for this ten minutes", and INSERT ... ON CONFLICT DO NOTHING is what decides who writes the entry.
/// </summary>
public interface IAuditReadLog
{
    /// <summary>The window within which a repeat of the same view is not written again.</summary>
    static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>True the first time for this person, view and filter in the window (and the caller should now write its entry); false for a repeat.</summary>
    Task<bool> ClaimAsync(Guid userId, string view, string filter, CancellationToken cancellationToken = default);

    /// <summary>Claims the view and, if it was the first, writes the standard read entry for it.</summary>
    Task NoteViewAsync(Guid userId, string view, string filter, string? remoteIpAddress, CancellationToken cancellationToken = default);
}
