using CaseRegister.Infrastructure.Persistence;
using Npgsql;

namespace CaseRegister.Api.Cli;

/// <summary>
/// <c>dotnet run -- migrate</c>: applies the migrations the database has not had yet. Production never does this by itself at startup
/// (a bad migration would then take the site down with it), and Render's free plan has no shell, so it is run from the
/// administrator's own machine with the owner connection string (ConnectionStrings__Migrations). It says which database it is about to change
/// and which migrations it will apply, and waits for a typed "yes". It never prints a connection string, only the host and database names.
/// </summary>
public static class MigrateCommand
{
    public const string Name = "migrate";

    public const int Succeeded = 0;
    public const int Refused = 1;

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, ITerminal terminal, CancellationToken cancellationToken = default)
    {
        if (args.Length > 1)
        {
            terminal.WriteLine($"{Name} takes no arguments. The database comes from {DatabaseOptions.MigrationsKey}.");

            return Refused;
        }

        if (!terminal.IsInteractive)
        {
            terminal.WriteLine($"{Name} needs an interactive terminal, so that someone confirms the database it is about to change.");

            return Refused;
        }

        var connection = configuration[DatabaseOptions.MigrationsKey];
        if (string.IsNullOrWhiteSpace(connection))
        {
            terminal.WriteLine($"{DatabaseOptions.MigrationsKey} is not configured. Set it to the database owner's direct connection string (with 'dotnet user-secrets' or the ConnectionStrings__Migrations environment variable).");

            return Refused;
        }

        var (host, database) = SeedAdminCommand.DatabaseOf(connection);
        terminal.WriteLine($"Database host '{host}', database '{database}'.");

        // Neon's pooled address (the one with -pooler in the host name) does not keep the session a migration needs.
        if (host.Contains("-pooler", StringComparison.OrdinalIgnoreCase))
        {
            terminal.WriteLine("That is the pooled address. Migrations need the direct connection string (the host without '-pooler'). Nothing was changed.");

            return Refused;
        }

        IReadOnlyList<string> pending;
        try
        {
            pending = await DatabaseMigrator.GetPendingMigrationsAsync(connection, cancellationToken);
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // The type only: the message can echo parts of the connection.
            terminal.WriteLine($"The database could not be reached or read ({exception.GetType().Name}). Nothing was changed.");

            return Refused;
        }

        if (pending.Count == 0)
        {
            terminal.WriteLine("There are no pending migrations. Nothing to do.");

            return Succeeded;
        }

        terminal.WriteLine($"{pending.Count} pending migration{(pending.Count == 1 ? string.Empty : "s")}, in the order they will be applied:");
        foreach (var name in pending)
        {
            terminal.WriteLine($"  {name}");
        }

        terminal.WriteLine("Have you taken a backup or a Neon restore point of this database? A migration cannot be undone by this command.");
        if (!string.Equals(terminal.ReadLine($"Type \"yes\" to apply them to '{database}' on '{host}': ")?.Trim(), "yes", StringComparison.Ordinal))
        {
            terminal.WriteLine("Nothing was applied.");

            return Refused;
        }

        await DatabaseMigrator.MigrateAsync(connection, cancellationToken);

        var left = await DatabaseMigrator.GetPendingMigrationsAsync(connection, cancellationToken);
        terminal.WriteLine(left.Count == 0
            ? $"Applied {pending.Count} migration{(pending.Count == 1 ? string.Empty : "s")}. The database is up to date."
            : $"Applied, but {left.Count} migration{(left.Count == 1 ? " is" : "s are")} still pending.");

        return left.Count == 0 ? Succeeded : Refused;
    }
}
