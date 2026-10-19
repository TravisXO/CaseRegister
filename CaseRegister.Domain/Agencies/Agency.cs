using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Agencies;

public enum AgencyStatus
{
    Active,
    Suspended,
}

/// <summary>An estate agency. Every agent belongs to exactly one; association staff belong to none.</summary>
public sealed class Agency : Entity
{
    public const int MaxNameLength = 200;

    // For EF Core.
    private Agency()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public AgencyStatus Status { get; private set; } = AgencyStatus.Active;

    /// <summary>The earliest date this agency's records may be removed (see <see cref="RetentionPolicy"/>). Null while it is active. Nothing is removed yet.</summary>
    public DateTimeOffset? RetainUntil { get; private set; }

    /// <summary>While set, no removal of this agency's records may be considered, whatever <see cref="RetainUntil"/> says.</summary>
    public bool LegalHold { get; set; }

    /// <summary>Takes away every agent capability from the agency's members. Nothing is deleted.</summary>
    public void Suspend(DateTimeOffset now)
    {
        if (Status != AgencyStatus.Active)
        {
            throw new InvalidOperationException("Only an active agency can be suspended.");
        }

        Status = AgencyStatus.Suspended;
        RetainUntil = RetentionPolicy.RetainUntil(Status, now);
    }

    public void Reactivate(DateTimeOffset now)
    {
        if (Status != AgencyStatus.Suspended)
        {
            throw new InvalidOperationException("Only a suspended agency can be reactivated.");
        }

        Status = AgencyStatus.Active;
        RetainUntil = RetentionPolicy.RetainUntil(Status, now);
    }

    public static Agency Create(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"An agency name is at most {MaxNameLength} characters.", nameof(name));
        }

        return new Agency { Name = trimmed };
    }
}
