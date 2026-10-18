using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.KeyProtection;

/// <summary>One thing <c>verify-keys</c> checked, and whether it held. The text never contains a key, a password or a certificate.</summary>
public sealed record KeyCheck(string Name, bool Passed, string Detail);

/// <summary>
/// Checks that the keys this deployment is configured with are the ones its data needs: the audit keys are loaded, the data protection
/// certificates can read every stored key and none is revoked, a real authenticator key decrypts, and the whole audit chain verifies.
/// Used by the <c>verify-keys</c> command, and run after any rotation or restore.
/// </summary>
public sealed class KeyVerifier
{
    private readonly IAuditKeyring _auditKeys;
    private readonly IPseudonymiser _pseudonyms;
    private readonly DataProtectionCertificates _certificates;
    private readonly IKeyManager _keyManager;
    private readonly AuthenticatorKeyProtector _authenticatorKeys;
    private readonly IAuditVerifier _audit;
    private readonly AppDbContext _db;

    public KeyVerifier(
        IAuditKeyring auditKeys,
        IPseudonymiser pseudonyms,
        DataProtectionCertificates certificates,
        IKeyManager keyManager,
        AuthenticatorKeyProtector authenticatorKeys,
        IAuditVerifier audit,
        AppDbContext db)
    {
        _auditKeys = auditKeys;
        _pseudonyms = pseudonyms;
        _certificates = certificates;
        _keyManager = keyManager;
        _authenticatorKeys = authenticatorKeys;
        _audit = audit;
        _db = db;
    }

    public async Task<IReadOnlyList<KeyCheck>> VerifyAsync(CancellationToken cancellationToken = default)
    {
        List<KeyCheck> checks =
        [
            new("Audit keys", true, $"{_auditKeys.Accepted.Count} key(s) loaded: {string.Join(", ", _auditKeys.Accepted.Select(key => key.Id).Order(StringComparer.Ordinal))}; signing with '{_auditKeys.Current.Id}'."),
            new("Pseudonymisation keys", true, $"{_pseudonyms.KeyIds.Count} key(s) loaded: {string.Join(", ", _pseudonyms.KeyIds)}; making labels with '{_pseudonyms.CurrentKeyId}'."),
            new("Data protection certificates", true, $"{_certificates.All.Count} certificate(s) loaded (1 current, {_certificates.Previous.Count} older); the current one is valid until {_certificates.Current.NotAfter:yyyy-MM-dd}."),
        ];

        var ring = await KeyRingInspector.InspectAsync(_db, _keyManager, cancellationToken);
        checks.Add(new(
            "Data protection key table",
            ring.IsHealthy,
            ring.IsHealthy
                ? $"{ring.KeyRows} key(s) stored, all readable with the configured certificates, none revoked."
                : $"{ring.KeyRows} key(s) stored, {ring.ReadableKeys} readable, {ring.RevocationRows} revocation row(s). Unreadable keys mean a certificate that protected them is not configured; revoked keys should never exist."));

        checks.Add(await SampleAuthenticatorAsync(cancellationToken));

        var chain = await _audit.VerifyAsync(fromLastGoodAnchor: false, cancellationToken);
        checks.Add(new(
            "Audit chain",
            chain.Intact,
            chain.Intact
                ? $"{chain.EntriesChecked} entries checked up to sequence {chain.HeadSequence}, {chain.AnchorsChecked} anchor(s) looked at."
                : $"BROKEN at sequence {chain.FirstBreak?.Sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unknown)"}: {chain.FirstBreak?.Kind}. {chain.FirstBreak?.Message}"));

        return checks;
    }

    private async Task<KeyCheck> SampleAuthenticatorAsync(CancellationToken cancellationToken)
    {
        // The oldest stored authenticator key is the likeliest to have been protected with an old data protection key.
        var sample = await _db.Authenticators.IgnoreQueryFilters().AsNoTracking().OrderBy(record => record.CreatedAt).Select(record => record.EncryptedKey).FirstOrDefaultAsync(cancellationToken);
        if (sample is null)
        {
            return new("Authenticator key sample", true, "No authenticator keys are stored yet, so there is nothing to decrypt.");
        }

        try
        {
            var key = _authenticatorKeys.Unprotect(sample);

            return new("Authenticator key sample", key.Length > 0, key.Length > 0 ? "The oldest stored authenticator key decrypts." : "The oldest stored authenticator key decrypted to nothing.");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return new("Authenticator key sample", false, "The oldest stored authenticator key could NOT be decrypted: the data protection key or certificate that protected it is missing.");
        }
    }
}
