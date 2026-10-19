using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.KeyProtection;

namespace CaseRegister.Api.Cli;

/// <summary>
/// <c>dotnet run -- verify-keys</c>: loads the keys this deployment is configured with and checks that the data really needs nothing else: the
/// audit keys, the data protection certificates and the table of keys they protect, a sample authenticator key, and the whole audit chain.
/// It changes nothing. Run it after any rotation, certificate change or restore. Exit code 0 only if every check passed.
/// </summary>
public static class KeyCommands
{
    public const string VerifyName = "verify-keys";

    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int Misused = 2;

    public static bool Handles(string? name) => name == VerifyName;

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, ITerminal terminal, CancellationToken cancellationToken = default)
    {
        if (args.Length > 1)
        {
            terminal.WriteLine($"Usage: {VerifyName}");

            return Misused;
        }

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CaseRegister.Infrastructure.Auditing.AuditAnchorGate>().Suppress();
        var checks = await scope.ServiceProvider.GetRequiredService<KeyVerifier>().VerifyAsync(cancellationToken);

        foreach (var check in checks)
        {
            terminal.WriteLine($"{(check.Passed ? "OK    " : "FAILED")} {check.Name}: {check.Detail}");
        }

        var failed = checks.Count(check => !check.Passed);
        terminal.WriteLine(failed == 0 ? "All keys check out." : $"{failed} check(s) failed. Do not rotate or delete anything until they are understood (docs/runbooks/key-recovery.md).");

        return failed == 0 ? Succeeded : Failed;
    }
}
