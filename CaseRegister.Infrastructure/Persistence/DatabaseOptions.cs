namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// The two connection strings. There is deliberately no default for either: a missing value must
/// stop the application rather than fall back to something unintended.
/// </summary>
public sealed class DatabaseOptions
{
    public const string DefaultKey = "ConnectionStrings:Default";
    public const string MigrationsKey = "ConnectionStrings:Migrations";

    /// <summary>Runtime connection, as the least-privilege application role (pooled host).</summary>
    public string? Default { get; set; }

    /// <summary>Schema-change connection, as the database owner (direct, non-pooled host).</summary>
    public string? Migrations { get; set; }
}
