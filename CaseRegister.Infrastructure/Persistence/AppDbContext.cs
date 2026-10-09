using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Fuzzy name search (similarity and trigram indexes) relies on pg_trgm.
        modelBuilder.HasPostgresExtension("pg_trgm");

        // Entity configurations (ApplyConfigurationsFromAssembly) are added here as entities arrive.
        // The shared conventions run last so they see every entity type.
        modelBuilder.ApplyEntityConventions();
    }
}
