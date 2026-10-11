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

    /// <summary>Takes away every agent capability from the agency's members. Nothing is deleted.</summary>
    public void Suspend()
    {
        if (Status != AgencyStatus.Active)
        {
            throw new InvalidOperationException("Only an active agency can be suspended.");
        }

        Status = AgencyStatus.Suspended;
    }

    public void Reactivate()
    {
        if (Status != AgencyStatus.Suspended)
        {
            throw new InvalidOperationException("Only a suspended agency can be reactivated.");
        }

        Status = AgencyStatus.Active;
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
