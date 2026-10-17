using System.Xml.Linq;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.KeyProtection;

/// <summary>What the data protection key table holds, and how much of it the configured certificates can read.</summary>
/// <param name="KeyRows">Rows that are keys.</param>
/// <param name="ReadableKeys">Keys the key manager could read with the configured certificates.</param>
/// <param name="RevocationRows">Rows that revoke a key. Nothing in this application writes one.</param>
public sealed record KeyRingStatus(int KeyRows, int ReadableKeys, int RevocationRows)
{
    /// <summary>Some stored keys cannot be read: a certificate that protected them is no longer configured.</summary>
    public int UnreadableKeys => Math.Max(0, KeyRows - ReadableKeys);

    public bool IsHealthy => UnreadableKeys == 0 && RevocationRows == 0;
}

public static class KeyRingInspector
{
    // The key manager lists a stored key even when it cannot decrypt it; asking for its descriptor is what decrypts.
    private static bool CanBeRead(IKey key)
    {
        try
        {
            return key.Descriptor is not null;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            return false;
        }
    }

    public static async Task<KeyRingStatus> InspectAsync(AppDbContext db, IKeyManager keyManager, CancellationToken cancellationToken)
    {
        var rows = await db.DataProtectionKeys.AsNoTracking().Select(row => row.Xml).ToListAsync(cancellationToken);

        var names = rows.Select(xml => xml is null ? string.Empty : XElement.Parse(xml).Name.LocalName).ToList();

        return new KeyRingStatus(
            names.Count(name => name == "key"),
            keyManager.GetAllKeys().Count(key => !key.IsRevoked && CanBeRead(key)),
            names.Count(name => name == "revocation"));
    }
}

/// <summary>
/// Stops the application starting if the database already holds data protection keys that the configured certificates cannot read.
/// Without this, the key manager would skip them and quietly make a brand new key: everything encrypted with the old ones (every session,
/// every authenticator key) would become unreadable with nothing to say why. A missing certificate must be a loud failure at startup.
/// (If the database cannot be reached just now, there is nothing to check and the application starts as it did; reading keys needs the database anyway.)
/// </summary>
internal sealed partial class KeyRingGuard(IServiceScopeFactory scopes, IKeyManager keyManager, ILogger<KeyRingGuard> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        KeyRingStatus status;
        try
        {
            using var scope = scopes.CreateScope();
            status = await KeyRingInspector.InspectAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), keyManager, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotChecked(logger, exception.GetType().Name);

            return;
        }

        if (status.UnreadableKeys > 0)
        {
            throw new InvalidOperationException(
                $"The database holds {status.KeyRows} data protection key(s) but the configured certificates can read only {status.ReadableKeys}. "
                + "A certificate that protected the stored keys is no longer configured, so starting would create new keys and make everything encrypted with the old ones unreadable. "
                + "Configure the missing certificate as DataProtection:Previous:<n> (see docs/runbooks/key-recovery.md), or the current one if it was replaced by mistake.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The data protection key ring could not be checked at startup ({Reason}).")]
    private static partial void LogNotChecked(ILogger logger, string reason);
}
