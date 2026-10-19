using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Security;

/// <summary>
/// Asks Have I Been Pwned first (which already tries again within a short limit). If it still cannot be reached, the password is checked against the
/// bundled list of the 100,000 most used breached passwords, and a <see cref="AuditAction.PasswordCheckFallbackUsed"/> entry says that the weaker
/// check was the one used. A password in the list is refused as breached; any other is accepted, because failing every sign-up while a
/// service is down is worse. The entry never contains the password or anything about it except whether it was found.
/// </summary>
internal sealed partial class ResilientBreachedPasswordChecker(HaveIBeenPwnedClient primary, IAuditLog audit, ILogger<ResilientBreachedPasswordChecker> logger) : IBreachedPasswordChecker
{
    public async Task<BreachCheckResult> CheckAsync(string password, CancellationToken cancellationToken = default)
    {
        var result = await primary.CheckAsync(password, cancellationToken);
        if (result != BreachCheckResult.Unavailable)
        {
            return result;
        }

        var found = BundledBreachList.Contains(password);

        try
        {
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.PasswordCheckFallbackUsed,
                    ActorUserId: null,
                    TargetType: "PasswordCheck",
                    Detail: found
                        ? "Have I Been Pwned could not be reached; the bundled list of the 100,000 most used breached passwords was used, and the password is in it"
                        : "Have I Been Pwned could not be reached; the bundled list of the 100,000 most used breached passwords was used, and the password is not in it"),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The check itself still counts; only the note about it is lost, and it is in the log.
            LogNotRecorded(logger, exception);
        }

        return found ? BreachCheckResult.Breached : BreachCheckResult.NotBreached;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The note that the bundled password list was used could not be written to the audit log.")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception);
}
