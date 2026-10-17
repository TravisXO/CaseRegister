using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Persistence;

namespace CaseRegister.Api.Cli;

/// <summary>
/// <c>dotnet run -- reset-two-factor</c> (and, for an admin who has to go, <c>suspend-admin</c>): break-glass commands for when the
/// two-admin process cannot work, run from the administrator's own machine like <c>seed-admin</c>. Each one shows which database it will
/// write to, asks for the person's email and a reason, asks for the email again to confirm, refuses if this process would sign the audit
/// log with a different key from the latest entry, allows one break-glass action per person per 24 hours, writes the audit log, and tells
/// every admin and the person (critical notifications). Nothing is taken from the command line.
/// </summary>
public static class BreakGlassCommands
{
    public const string ResetTwoFactorName = "reset-two-factor";

    public const int Succeeded = 0;
    public const int Refused = 1;

    public static bool Handles(string? name) => name is ResetTwoFactorName;

    public static async Task<int> RunAsync(
        string[] args,
        IServiceProvider services,
        IConfiguration configuration,
        ITerminal terminal,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var breakGlass = scope.ServiceProvider.GetRequiredService<IBreakGlassService>();
        var command = args[0];

        if (args.Length > 1)
        {
            terminal.WriteLine($"{command} takes no arguments. The details are asked for.");
            return await RefuseAsync(audit, terminal, command, "arguments were given", cancellationToken);
        }

        if (!terminal.IsInteractive)
        {
            terminal.WriteLine($"{command} needs an interactive terminal.");
            return await RefuseAsync(audit, terminal, command, "not an interactive terminal", cancellationToken);
        }

        var migrations = configuration[DatabaseOptions.MigrationsKey];
        if (string.IsNullOrWhiteSpace(migrations))
        {
            terminal.WriteLine($"{DatabaseOptions.MigrationsKey} is not configured, so migrations cannot be checked. Set it with 'dotnet user-secrets'.");
            return await RefuseAsync(audit, terminal, command, "migrations connection not configured", cancellationToken);
        }

        if (await DatabaseMigrator.HasPendingMigrationsAsync(migrations, cancellationToken))
        {
            terminal.WriteLine("There are migrations that have not been applied to this database. Apply them first.");
            return await RefuseAsync(audit, terminal, command, "migrations pending", cancellationToken);
        }

        // Signing with another key would put an entry in the chain that its verifier will not accept: the wrong settings are in use here.
        if (await breakGlass.CheckAuditKeyAsync(cancellationToken) is { } keyProblem)
        {
            terminal.WriteLine(keyProblem);
            return Refused; // not written: this process cannot sign an entry the chain would accept
        }

        var (host, database) = SeedAdminCommand.DatabaseOf(configuration[DatabaseOptions.DefaultKey]);
        terminal.WriteLine($"This will change accounts on database host '{host}' (database '{database}').");

        var email = (terminal.ReadLine("Email of the person: ") ?? string.Empty).Trim();
        var target = email.Length == 0 ? null : await breakGlass.FindAsync(email, cancellationToken);
        if (target is null)
        {
            terminal.WriteLine("There is no account with that email.");
            return await RefuseAsync(audit, terminal, command, "no such account", cancellationToken);
        }

        terminal.WriteLine($"{target.FullName} ({(target.IsAdmin ? "an admin" : "not an admin")}).");

        var reason = (terminal.ReadLine("Reason (how their identity was checked, and why): ") ?? string.Empty).Trim();
        if (ApprovalRules.CheckIdentityReason(reason) is { } badReason)
        {
            terminal.WriteLine(badReason.Message);
            return await RefuseAsync(audit, terminal, command, "reason missing or too short", cancellationToken);
        }

        if (!string.Equals(terminal.ReadLine("Type the person's email again to confirm: ")?.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            terminal.WriteLine("That is not the same email. Nothing was changed.");
            return await RefuseAsync(audit, terminal, command, "not confirmed", cancellationToken);
        }

        var result = await breakGlass.ResetTwoFactorAsync(target.UserId, reason, cancellationToken);

        switch (result.Outcome)
        {
            case BreakGlassOutcome.Done:
                terminal.WriteLine("Two-factor was removed, the person's sessions were ended, and every admin and the person have been told.");
                terminal.WriteLine($"One-time enrolment code (valid {(int)Application.Auth.EnrolmentCodes.Lifetime.TotalHours} hours, shown only now): {result.EnrolmentCode}");
                terminal.WriteLine("Give it to the person in person or by phone. They need it, with their password, to set up two-factor again.");
                return Succeeded;

            default:
                terminal.WriteLine(result.Message ?? "That is not possible now. Nothing was changed.");
                return await RefuseAsync(audit, terminal, command, result.Outcome.ToString(), cancellationToken);
        }
    }

    private static async Task<int> RefuseAsync(IAuditLog audit, ITerminal terminal, string command, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(new AuditEntry(AuditAction.BreakGlassRefused, ActorUserId: null, Detail: $"{command} did not run: {reason}"), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            terminal.WriteLine("The refusal could not be written to the audit log.");
        }

        return Refused;
    }
}
