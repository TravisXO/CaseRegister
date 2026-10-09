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
