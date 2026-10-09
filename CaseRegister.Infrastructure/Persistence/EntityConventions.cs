using CaseRegister.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>Mapping rules applied to every entity that derives from <see cref="Entity"/>.</summary>
public static class EntityConventions
{
    public static ModelBuilder ApplyEntityConventions(this ModelBuilder modelBuilder)
    {
        var roots = modelBuilder.Model.GetEntityTypes()
            .Where(type => type.BaseType is null && typeof(Entity).IsAssignableFrom(type.ClrType))
            .Select(type => type.ClrType)
            .ToList();

        foreach (var clrType in roots)
        {
            var entity = modelBuilder.Entity(clrType);

            // The entity assigns its own version 7 GUID, so the database must never generate one.
            entity.Property(nameof(Entity.Id)).ValueGeneratedNever();

            // Always timestamptz, always UTC (Npgsql rejects a DateTimeOffset with a non-zero offset).
            entity.Property(nameof(Entity.CreatedAt)).HasColumnType("timestamp with time zone");
            entity.Property(nameof(Entity.UpdatedAt)).HasColumnType("timestamp with time zone");

            // Optimistic concurrency on the Postgres system column xmin: no column to maintain, and it
            // changes on every update of the row.
            entity.Property(nameof(Entity.Version))
                .HasColumnName("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        return modelBuilder;
    }
}
