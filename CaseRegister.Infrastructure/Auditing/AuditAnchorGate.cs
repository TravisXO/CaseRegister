namespace CaseRegister.Infrastructure.Auditing;

/// <summary>
/// Lets a command that only reads the anchors (verify-audit, verify-keys) say that it must not store any. Those are run from the administrator's
/// machine with a read-only token for the anchor bucket; the audit entry that records the check would otherwise ask for an anchor and fail to store it.
/// </summary>
public sealed class AuditAnchorGate
{
    public bool Suppressed { get; private set; }

    public void Suppress() => Suppressed = true;
}
