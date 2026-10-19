using System.Security.Cryptography;
using System.Text;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Auditing;

/// <summary>The audit labels for email addresses: HMAC-SHA256 under the current pseudonymisation key, with its id in the label. See <see cref="PseudonymOptions"/>.</summary>
internal sealed class Pseudonymiser : IPseudonymiser
{
    private readonly byte[] _current;

    public Pseudonymiser(IOptions<PseudonymOptions> options)
    {
        var settings = options.Value;
        if (settings.Problem() is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        CurrentKeyId = settings.CurrentKeyId;
        _current = Convert.FromBase64String(settings.Keys[settings.CurrentKeyId]);
        KeyIds = [.. settings.Keys.Keys.Order(StringComparer.Ordinal)];
    }

    public string CurrentKeyId { get; }

    public IReadOnlyCollection<string> KeyIds { get; }

    public string ForEmail(string? email)
    {
        var normalised = EmailNormaliser.Normalise(email ?? string.Empty);
        var mac = HMACSHA256.HashData(_current, Encoding.UTF8.GetBytes(normalised));

        // 16 bytes are plenty to tell addresses apart; the rest adds length and nothing else.
        return $"{AuditSubject.EmailHmacPrefix}{CurrentKeyId}:{Convert.ToHexString(mac.AsSpan(0, 16)).ToLowerInvariant()}";
    }
}
