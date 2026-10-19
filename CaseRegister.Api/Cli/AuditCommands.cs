using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Auditing;

namespace CaseRegister.Api.Cli;

/// <summary>
/// <c>dotnet run -- verify-audit [--from-anchor] [--restored-backup]</c> checks the audit chain and anchors, and
/// <c>dotnet run -- anchor-audit</c> writes an anchor of the chain as it stands. Both are themselves written to the audit log.
/// </summary>
public static class AuditCommands
{
    public const string VerifyName = "verify-audit";
    public const string AnchorName = "anchor-audit";
    public const string FromAnchorOption = "--from-anchor";
    public const string RestoredBackupOption = "--restored-backup";

    public const int Succeeded = 0;
    public const int Failed = 1;
    public const int Misused = 2;

    public static bool Handles(string? name) => name is VerifyName or AnchorName;

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, ITerminal terminal, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        return args[0] == VerifyName
            ? await VerifyAsync(args, scope.ServiceProvider, terminal, cancellationToken)
            : await AnchorAsync(args, scope.ServiceProvider, terminal, cancellationToken);
    }

    private static async Task<int> VerifyAsync(string[] args, IServiceProvider services, ITerminal terminal, CancellationToken cancellationToken)
    {
        // Reading anchors is all this does (with a read-only token, see docs/deployment.md), so the entry that records it asks for no anchor to be stored.
        services.GetRequiredService<AuditAnchorGate>().Suppress();
        var extra = args.Skip(1).ToList();
        if (extra.Any(argument => argument is not (FromAnchorOption or RestoredBackupOption)))
        {
            terminal.WriteLine($"Usage: {VerifyName} [{FromAnchorOption}] [{RestoredBackupOption}]");

            return Misused;
        }

        var result = await services.GetRequiredService<IAuditVerifier>().VerifyAndRecordAsync(extra.Contains(FromAnchorOption), actorUserId: null, remoteIpAddress: null, restoredBackup: extra.Contains(RestoredBackupOption), cancellationToken: cancellationToken);

        terminal.WriteLine($"Checked {result.EntriesChecked} entries up to sequence {result.HeadSequence}"
            + (result.FromAnchorSequence is { } from ? $", starting after the anchor at {from}" : ", from the first entry")
            + $"; {result.AnchorsChecked} anchors looked at"
            + (result.AnchorStoreConfigured ? "." : " (no anchor store is configured).")
            + (result.NewerAnchors > 0 ? $" {result.NewerAnchors} anchors are newer than this copy and could not be compared." : string.Empty));

        if (result.Intact)
        {
            terminal.WriteLine("The audit chain is intact.");

            return Succeeded;
        }

        terminal.WriteLine($"BROKEN at sequence {result.FirstBreak!.Sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unknown)"}: {result.FirstBreak.Kind}. {result.FirstBreak.Message}");

        return Failed;
    }

    private static async Task<int> AnchorAsync(string[] args, IServiceProvider services, ITerminal terminal, CancellationToken cancellationToken)
    {
        if (args.Length > 1)
        {
            terminal.WriteLine($"Usage: {AnchorName}");

            return Misused;
        }

        var anchor = await services.GetRequiredService<IAuditAnchoring>().AnchorNowAsync(actorUserId: null, cancellationToken);

        if (anchor is null)
        {
            terminal.WriteLine("Nothing was anchored: either the audit log is empty or no anchor store is configured (Audit:R2:* in production, Audit:AnchorFilePath in Development).");

            return Failed;
        }

        terminal.WriteLine($"Anchored sequence {anchor.Sequence} (hash {anchor.Hash[..12]}...) for {anchor.ZambianDate}.");

        return Succeeded;
    }
}
