namespace CaseRegister.Domain.Common;

/// <summary>
/// Base for every persisted entity. Holds only the columns all entities share; there is no
/// deeper hierarchy. Infrastructure maps and maintains these members (see its entity conventions).
/// </summary>
public abstract class Entity
{
    protected Entity()
    {
        Id = Guid.CreateVersion7();
    }

    /// <summary>Public identifier. A version 7 GUID, so values sort by creation time.</summary>
    public Guid Id { get; private set; }

    /// <summary>When the record was created, in UTC. Set by Infrastructure on insert.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the record was last changed, in UTC. Set by Infrastructure on insert and update.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Postgres maintains it (the system column xmin), so an update
    /// based on a stale copy of the record is rejected instead of silently overwriting newer changes.
    /// </summary>
    public uint Version { get; private set; }
}
