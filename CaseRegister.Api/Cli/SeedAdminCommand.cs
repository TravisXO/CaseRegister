using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Npgsql;

namespace CaseRegister.Api.Cli;

/// <summary>What the command talks to. The real one is the console; tests give it scripted answers.</summary>
public interface ITerminal
{
    /// <summary>False when input is piped or redirected. A password cannot be masked there, so the command refuses.</summary>
    bool IsInteractive { get; }

    string? ReadLine(string prompt);

    /// <summary>Reads a secret without showing it.</summary>
    string? ReadSecret(string prompt);

    void WriteLine(string text);
}

public sealed class ConsoleTerminal : ITerminal
{
    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public string? ReadLine(string prompt)
    {
        Console.Write(prompt);

        return Console.ReadLine();
    }

    public string? ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var typed = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    public void WriteLine(string text) => Console.WriteLine(text);
}

/// <summary>
/// <c>dotnet run -- seed-admin</c>: creates the first admins, because approving anyone needs two admins to
/// exist already. It is deliberately awkward to misuse: the password is only ever typed at a masked prompt
/// (never an argument or a pipe), it shows which database it will write to and waits for a typed "yes", it
/// refuses while migrations are pending, and it stops for good once two active admins exist. Every run, whether it
/// creates anyone or not, is written to the audit log.
/// </summary>
public static class SeedAdminCommand
{
    public const string Name = "seed-admin";

    private const int PasswordAttempts = 3;

    public const int Succeeded = 0;
    public const int Refused = 1;

    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        IConfiguration configuration,
        ITerminal terminal,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();

        // Nothing is taken from the command line: a password in an argument ends up in shell history and process lists.
        if (args.Length > 1)
        {
            terminal.WriteLine($"{Name} takes no arguments. The details are asked for, and the password only at a masked prompt.");
            return await RefuseAsync(audit, terminal, "arguments were given", cancellationToken);
        }

        if (!terminal.IsInteractive)
        {
            terminal.WriteLine($"{Name} needs an interactive terminal, so the password can be typed without being shown.");
            return await RefuseAsync(audit, terminal, "not an interactive terminal", cancellationToken);
        }

        // Writing to a database whose schema is behind would fail, or worse, half work.
        var migrations = configuration[DatabaseOptions.MigrationsKey];
        if (string.IsNullOrWhiteSpace(migrations))
        {
            terminal.WriteLine($"{DatabaseOptions.MigrationsKey} is not configured, so migrations cannot be checked. Set it with 'dotnet user-secrets'.");
            return await RefuseAsync(audit, terminal, "migrations connection not configured", cancellationToken);
        }

        if (await DatabaseMigrator.HasPendingMigrationsAsync(migrations, cancellationToken))
        {
            terminal.WriteLine("There are migrations that have not been applied to this database. Apply them first.");
            return await RefuseAsync(audit, terminal, "migrations pending", cancellationToken);
        }

        var seeder = scope.ServiceProvider.GetRequiredService<IAdminSeeder>();
        var active = await seeder.CountActiveAdminsAsync(cancellationToken);
        if (active >= ApprovalRules.MinimumActiveAdmins)
        {
            terminal.WriteLine($"There are already {active} active admins. This command only works while there are fewer than {ApprovalRules.MinimumActiveAdmins}; use the admin approval process from now on.");
            return await RefuseAsync(audit, terminal, $"{active} active admins already", cancellationToken);
        }

        terminal.WriteLine($"There {(active == 1 ? "is" : "are")} {active} active admin{(active == 1 ? string.Empty : "s")}. This will create one more.");

        var email = (terminal.ReadLine("Email: ") ?? string.Empty).Trim();
        var fullName = (terminal.ReadLine("Full name: ") ?? string.Empty).Trim();
        if (email.Length == 0 || fullName.Length == 0)
        {
            terminal.WriteLine("An email and a full name are needed.");
            return await RefuseAsync(audit, terminal, "details missing", cancellationToken);
        }

        var password = await AskForPasswordAsync(seeder, terminal, email, fullName, cancellationToken);
        if (password is null)
        {
            return await RefuseAsync(audit, terminal, "no acceptable password given", cancellationToken);
        }

        // The last look before anything is written: where it goes, never the password.
        var (host, database) = DatabaseOf(configuration[DatabaseOptions.DefaultKey]);
        terminal.WriteLine($"This will create an admin on database host '{host}' (database '{database}').");
        if (!string.Equals(terminal.ReadLine("Type \"yes\" to continue: ")?.Trim(), "yes", StringComparison.Ordinal))
        {
            terminal.WriteLine("Nothing was created.");
            return await RefuseAsync(audit, terminal, "not confirmed", cancellationToken);
        }

        var result = await seeder.SeedAsync(new SeedAdminRequest(email, fullName, password), cancellationToken);

        switch (result.Outcome)
        {
            case SeedAdminOutcome.Created:
                terminal.WriteLine("The admin was created. They have no authenticator yet, so they must set up two-factor when they first sign in.");
                return Succeeded;
            case SeedAdminOutcome.EnoughAdmins:
                terminal.WriteLine("There are already enough admins. Nothing was created.");
                return await RefuseAsync(audit, terminal, "enough admins by the time of writing", cancellationToken);
            default:
                foreach (var error in result.Errors)
                {
                    terminal.WriteLine(error);
                }

                terminal.WriteLine("Nothing was created.");
                return await RefuseAsync(audit, terminal, "the account was not accepted", cancellationToken);
        }
    }

    private static async Task<string?> AskForPasswordAsync(IAdminSeeder seeder, ITerminal terminal, string email, string fullName, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PasswordAttempts; attempt++)
        {
            var password = terminal.ReadSecret("Password: ") ?? string.Empty;
            var again = terminal.ReadSecret("Password again: ") ?? string.Empty;

            if (!string.Equals(password, again, StringComparison.Ordinal))
            {
                terminal.WriteLine("The two passwords are not the same.");
                continue;
            }

            // The same rules as every account: length, not guessable from the person's details, not in a breach.
            var problems = await seeder.CheckPasswordAsync(email, fullName, password, cancellationToken);
            if (problems.Count == 0)
            {
                return password;
            }

            foreach (var problem in problems)
            {
                terminal.WriteLine(problem);
            }
        }

        terminal.WriteLine("No acceptable password was given.");

        return null;
    }

    /// <summary>The host only: never the user, the password or anything else from the connection string.</summary>
    public static (string Host, string Database) DatabaseOf(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return ("(not configured)", "(not configured)");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);

        return (builder.Host ?? "(unknown)", builder.Database ?? "(unknown)");
    }

    private static async Task<int> RefuseAsync(IAuditLog audit, ITerminal terminal, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(new AuditEntry(AuditAction.AdminSeedRefused, ActorUserId: null, Detail: $"{Name} did not create an admin: {reason}"), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            terminal.WriteLine("The refusal could not be written to the audit log.");
        }

        return Refused;
    }
}
