using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CaseRegister.Tests.Testing;

/// <summary>
/// A software authenticator, for tests: it does what a phone or a security key does for WebAuthn (makes an ES256 credential, signs challenges),
/// and writes its answers in the JSON a browser's <c>PublicKeyCredential.toJSON()</c> gives. Whether it reports that it verified the user
/// (a PIN, a fingerprint) is up to the test, which is how "no user verification is rejected" is proved.
/// </summary>
public sealed class VirtualAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string[] _transports = ["internal"];
    private uint _signCount;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public string Origin { get; init; } = ApiFactory.PasskeyOrigin;

    public string RelyingPartyId { get; init; } = ApiFactory.PasskeyRelyingPartyId;

    /// <summary>The user handle the service gave when the credential was made; it comes back with every sign-in.</summary>
    public string? UserHandle { get; private set; }

    public string CredentialIdText => Base64Url.EncodeToString(CredentialId);

    /// <summary>The answer to creation options (what the browser sends to add a passkey).</summary>
    public string Register(string creationOptionsJson, bool userVerified = true, string? originOverride = null)
    {
        using var options = JsonDocument.Parse(creationOptionsJson);
        var root = options.RootElement;
        UserHandle = root.GetProperty("user").GetProperty("id").GetString();

        var clientData = ClientData("webauthn.create", root.GetProperty("challenge").GetString()!, originOverride ?? Origin);
        var authData = AuthenticatorData(RelyingPartyId, userVerified, attested: true);
        var attestationObject = Cbor.Map(
            ("fmt", Cbor.Text("none")),
            ("attStmt", Cbor.Map()),
            ("authData", Cbor.Bytes(authData)));

        return JsonSerializer.Serialize(new
        {
            id = CredentialIdText,
            rawId = CredentialIdText,
            type = "public-key",
            authenticatorAttachment = "platform",
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                attestationObject = Base64Url.EncodeToString(attestationObject),
                transports = _transports,
            },
            clientExtensionResults = new { },
        });
    }

    /// <summary>The answer to request options (what the browser sends to sign in, or for a step-up).</summary>
    public string Assert(string requestOptionsJson, bool userVerified = true, string? originOverride = null, bool includeUserHandle = true)
    {
        using var options = JsonDocument.Parse(requestOptionsJson);
        var root = options.RootElement;

        var clientData = ClientData("webauthn.get", root.GetProperty("challenge").GetString()!, originOverride ?? Origin);
        _signCount++;
        var authData = AuthenticatorData(RelyingPartyId, userVerified, attested: false);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return JsonSerializer.Serialize(new
        {
            id = CredentialIdText,
            rawId = CredentialIdText,
            type = "public-key",
            authenticatorAttachment = "platform",
            response = new
            {
                clientDataJSON = Base64Url.EncodeToString(clientData),
                authenticatorData = Base64Url.EncodeToString(authData),
                signature = Base64Url.EncodeToString(signature),
                userHandle = includeUserHandle ? UserHandle : null,
            },
            clientExtensionResults = new { },
        });
    }

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

    private byte[] AuthenticatorData(string relyingPartyId, bool userVerified, bool attested)
    {
        // Flags: user present (1), user verified (4), attested credential data included (64).
        var flags = (byte)(1 | (userVerified ? 4 : 0) | (attested ? 64 : 0));
        List<byte> data = [.. SHA256.HashData(Encoding.UTF8.GetBytes(relyingPartyId)), flags, .. BitConverter.GetBytes(_signCount).Reverse()];

        if (attested)
        {
            var parameters = _key.ExportParameters(false);
            var cose = Cbor.IntMap(
                (1, Cbor.Int(2)), // key type: EC2
                (3, Cbor.Int(-7)), // algorithm: ES256
                (-1, Cbor.Int(1)), // curve: P-256
                (-2, Cbor.Bytes(parameters.Q.X!)),
                (-3, Cbor.Bytes(parameters.Q.Y!)));

            data.AddRange(new byte[16]); // the authenticator model (all zeros: none)
            data.AddRange([(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length]);
            data.AddRange(CredentialId);
            data.AddRange(cose);
        }

        return [.. data];
    }

    public void Dispose() => _key.Dispose();

    /// <summary>Just enough CBOR (RFC 8949) for an attestation object and a COSE key.</summary>
    private static class Cbor
    {
        public static byte[] Int(long value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);

            return [.. Head(3, (ulong)bytes.Length), .. bytes];
        }

        public static byte[] Map(params (string Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(entry => Text(entry.Key).Concat(entry.Value))];

        public static byte[] IntMap(params (long Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(entry => Int(entry.Key).Concat(entry.Value))];

        private static byte[] Head(int major, ulong value) => value switch
        {
            < 24 => [(byte)((major << 5) | (int)value)],
            < 256 => [(byte)((major << 5) | 24), (byte)value],
            < 65536 => [(byte)((major << 5) | 25), (byte)(value >> 8), (byte)value],
            _ => [(byte)((major << 5) | 26), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value],
        };
    }
}
