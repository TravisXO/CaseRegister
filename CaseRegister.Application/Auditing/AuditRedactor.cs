using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CaseRegister.Application.Auditing;

/// <summary>
/// Makes the before and after values of an audit entry safe to keep for ever: anything that looks like a
/// password, a token, a code, a key or an NRC is replaced, and a value that is too large is cut down. The
/// result is canonical JSON text, ready to be hashed.
/// </summary>
public static partial class AuditRedactor
{
    public const string Redacted = "[redacted]";
    public const int MaxBytes = 4096;

    // A property with one of these in its name never has its value kept.
    private static readonly string[] _secretNames =
    [
        "password", "passwd", "secret", "token", "otp", "twofactorcode", "recoverycode", "code", "nrc", "passport",
        "narrative", "apikey", "authorization", "cookie", "connectionstring", "privatekey", "credential", "sessionid", "bearer", "hmac", "hash",
    ];

    // Names that contain "code" or "hash" innocently.
    private static readonly string[] _allowedNames = ["payloadhash", "postcode", "errorcode", "refusalcode", "tokenhash"];

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Redacted, capped, canonical JSON text for a value; null for no value.</summary>
    public static string? Prepare(object? value, int maxBytes = MaxBytes)
    {
        if (value is null)
        {
            return null;
        }

        var node = value is JsonNode existing ? existing.DeepClone() : JsonSerializer.SerializeToNode(value, _json);
        node = RedactNode(node);
        var text = Jcs.Canonicalise(node);

        if (Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        // Too big to keep whole: say so, and keep a short start of it so the entry is still readable.
        var preview = text.Length > 200 ? text[..200] : text;

        return Jcs.Canonicalise(new JsonObject
        {
            ["truncated"] = true,
            ["originalBytes"] = Encoding.UTF8.GetByteCount(text),
            ["preview"] = preview,
        });
    }

    public static bool IsSecretName(string name)
    {
        var lower = name.ToLowerInvariant().Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

        return !_allowedNames.Contains(lower) && _secretNames.Any(secret => lower.Contains(secret, StringComparison.Ordinal));
    }

    private static JsonNode? RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                var result = new JsonObject();
                foreach (var property in jsonObject)
                {
                    result[property.Key] = IsSecretName(property.Key) ? JsonValue.Create(Redacted) : RedactNode(property.Value?.DeepClone());
                }

                return result;

            case JsonArray jsonArray:
                var items = new JsonArray();
                foreach (var item in jsonArray)
                {
                    items.Add(RedactNode(item?.DeepClone()));
                }

                return items;

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return LooksSecret(value.GetValue<string>()) ? JsonValue.Create(Redacted) : value;

            default:
                return node;
        }
    }

    /// <summary>Values that are secrets whatever they are called: an NRC, or a long random token or session id.</summary>
    private static bool LooksSecret(string text) => NationalRegistration().IsMatch(text) || LongToken().IsMatch(text);

    [GeneratedRegex(@"\b\d{6}/\d{2}/\d\b")]
    private static partial Regex NationalRegistration();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{40,}$")]
    private static partial Regex LongToken();
}
