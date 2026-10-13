using System.Text.RegularExpressions;
using CaseRegister.Application.Auditing;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace CaseRegister.Api.Hardening;

/// <summary>
/// Takes passwords, tokens, codes, cookies, NRCs, connection strings and email addresses out of text on its way to the logs.
/// Secrets are caught by what they are called ("password=...", "token": "...") and by what they look like.
/// </summary>
public static partial class LogRedactor
{
    public const string Redacted = "[redacted]";

    // Where the database is. EF Core names both in its connection errors.
    private static readonly string[] _hiddenNames = ["server", "database", "host"];

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = UriWithCredentials().Replace(text, "[redacted-connection]");
        text = DatabaseTarget().Replace(text, "${name} '" + Redacted + "'");
        text = TcpAddress().Replace(text, "[redacted-address]");
        text = HostAndPort().Replace(text, "[redacted-address]");
        text = Ipv4Address().Replace(text, "[redacted-address]");
        text = ConnectionStringPair().Replace(text, "${key}=" + Redacted);
        text = CookieHeader().Replace(text, "${name}: " + Redacted);
        text = AppCookie().Replace(text, "${name}=" + Redacted);
        text = Bearer().Replace(text, "Bearer " + Redacted);
        text = PasswordValue().Replace(text, "${name}${separator}" + Redacted);
        text = NamedSecret().Replace(text, "${name}${separator}" + Redacted);
        text = Nrc().Replace(text, "[redacted-nrc]");
        text = EmailAddress().Replace(text, "[redacted-email]");
        text = LongToken().Replace(text, "[redacted-token]");

        return text;
    }

    /// <summary>The value of a log property: dropped entirely if its name says it is a secret, otherwise cleaned like any other text.</summary>
    public static LogEventPropertyValue RedactValue(string name, LogEventPropertyValue value)
    {
        if (AuditRedactor.IsSecretName(name) || _hiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return new ScalarValue(Redacted);
        }

        return value switch
        {
            ScalarValue { Value: string text } => new ScalarValue(Redact(text)),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => RedactValue(name, element))),
            StructureValue structure => new StructureValue(structure.Properties.Select(property => new LogEventProperty(property.Name, RedactValue(property.Name, property.Value))), structure.TypeTag),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(pair => KeyValuePair.Create(pair.Key, RedactValue(pair.Key.Value?.ToString() ?? string.Empty, pair.Value)))),
            _ => value,
        };
    }

    // user:password@host in a URL, such as postgres://user:secret@host/db
    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s/:@]+:[^\s/@]*@[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UriWithCredentials();

    // Host=...;Password=...; the parts of a connection string that identify or open the database.
    [GeneratedRegex(@"\b(?<key>host|server|data source|username|user id|uid|password|pwd|database|port)\s*=\s*[^;\r\n""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPair();

    // Cookie: a=b; c=d   and   Set-Cookie: ...
    [GeneratedRegex(@"\b(?<name>set-cookie|cookie)\s*:\s*[^\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex CookieHeader();

    // The names of this application's own cookies, wherever they turn up.
    [GeneratedRegex(@"(?<name>__Host-cr(?:-2fa(?:-remember)?)?|__Host-csrf)\s*=\s*[^;\s]+")]
    private static partial Regex AppCookie();

    // database '"neondb"' on server '"tcp://host:5432"': the way EF Core words a connection error.
    [GeneratedRegex("""\b(?<name>database|server)\s+'+"?[^'"]*"?'+""", RegexOptions.IgnoreCase)]
    private static partial Regex DatabaseTarget();

    [GeneratedRegex("""\btcp://[^\s'"]+""", RegexOptions.IgnoreCase)]
    private static partial Regex TcpAddress();

    // "Failed to connect to ep-name.neon.tech:5432", the way a driver words it.
    [GeneratedRegex(@"\b(?:[A-Za-z0-9-]+\.)+[A-Za-z0-9-]+:\d{2,5}\b")]
    private static partial Regex HostAndPort();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Address();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();


    // A password can be a phrase with spaces in it, so an unquoted one runs to the end of the line (or the next ; , & or }).
    // The name may be followed by quotes, with a backslash before each when the text is a string inside another string.
    [GeneratedRegex(
        """(?<name>\b(?:password|passwd|pwd|passphrase)[\\"']*)(?<separator>\s*[:=]\s*)(?:\\*"[^"\\]*\\*"|'[^']*'|[^;,&}"'\\\r\n]+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex PasswordValue();

    // token=abc, "token": "abc" (also with the quotes escaped), code: 123456, recoveryCode=abc ...
    [GeneratedRegex(
        """(?<name>\b(?:secret|token|apikey|api_key|otp|totp|code|recoverycode|recovery_code|authorization|connectionstring|nrc|passport)[\\"']*)(?<separator>\s*[:=]\s*)(?:\\*"[^"\\]*\\*"|'[^']*'|[^\s,;&}"'\\]+)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"\b\d{6}/\d{2}/\d\b")]
    private static partial Regex Nrc();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"[A-Za-z0-9_\-]{40,}")]
    private static partial Regex LongToken();
}

/// <summary>
/// Passes every event on with its message, properties and exception cleaned. An exception cannot be edited, so its text
/// (type and message, and the stack) is cleaned and carried in a property instead.
/// </summary>
public sealed class RedactingSink(ILogEventSink inner) : ILogEventSink
{
    public const string ExceptionProperty = "Exception";

    // The host adds the raw address of the request to every line written during it. The route template (in the request line) says
    // which endpoint it was; the address itself can carry identifiers and is not wanted.
    private static readonly string[] _droppedProperties = ["RequestPath"];

    public void Emit(LogEvent logEvent)
    {
        var properties = logEvent.Properties
            .Where(pair => pair.Key != ExceptionProperty && !_droppedProperties.Contains(pair.Key))
            .Select(pair => new LogEventProperty(pair.Key, LogRedactor.RedactValue(pair.Key, pair.Value)))
            .ToList();

        // The message is written from the cleaned properties, so a value passed as {Password} is not in it, and then the text itself is cleaned.
        var message = LogRedactor.Redact(logEvent.MessageTemplate.Render(properties.ToDictionary(property => property.Name, property => property.Value), System.Globalization.CultureInfo.InvariantCulture));

        if (logEvent.Exception is { } exception)
        {
            properties.Add(new LogEventProperty(ExceptionProperty, new ScalarValue(LogRedactor.Redact(exception.ToString()))));
        }

        // The message is now plain text. Braces in it are escaped so it is not read as a template again.
        var template = new MessageTemplate([new TextToken(message)]);

        inner.Emit(new LogEvent(logEvent.Timestamp, logEvent.Level, null, template, properties, logEvent.TraceId ?? default, logEvent.SpanId ?? default));
    }
}
