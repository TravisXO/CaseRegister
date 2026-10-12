using CaseRegister.Api.Hardening;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace CaseRegister.Tests.Hardening;

/// <summary>Every kind of secret is thrown at the logger, in the message, in a property and in an exception, and none comes out.</summary>
public sealed class LogRedactionTests
{
    // The secret itself, then the text it is hidden in.
    public static TheoryData<string, string> Secrets => new()
    {
        // passwords
        { "hunter2-hunter2", "login failed, password=hunter2-hunter2" },
        { "correct horse battery", "Password: correct horse battery" },
        { "s3cret!pass", """{"password":"s3cret!pass","email":"a"}""" },
        { "p@ss-word-99", "connect pwd=p@ss-word-99 now" },

        // tokens
        { "eyJhbGciOiJIUzI1NiJ9", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature" },
        { "abcdef123456789", "refresh token=abcdef123456789 rejected" },
        { "tok-value-in-json", """{"token": "tok-value-in-json"}""" },
        { "AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKK", "a bare AAAABBBBCCCCDDDDEEEEFFFFGGGGHHHHIIIIJJJJKKKK in the middle" },
        { "my-api-key-value", "apikey=my-api-key-value" },

        // codes
        { "123456", "two-factor code=123456 was wrong" },
        { "987654", "otp: 987654" },
        { "ABCD-1234-EFGH", "recoveryCode=ABCD-1234-EFGH used" },
        { "111222", "totp=111222" },

        // cookies
        { "CfDJ8SessionValueOne", "Cookie: __Host-cr=CfDJ8SessionValueOne; __Host-csrf=AntiForgeryTwo" },
        { "CfDJ8SessionValueTwo", "Set-Cookie: __Host-cr=CfDJ8SessionValueTwo; path=/; secure; httponly" },
        { "CsrfCookieValueThree", "the csrf cookie __Host-csrf=CsrfCookieValueThree was sent" },
        { "TwoFactorStepValue", "__Host-cr-2fa=TwoFactorStepValue" },

        // national registration cards
        { "123456/78/9", "applicant 123456/78/9 not found" },
        { "654321/10/1", "nrc=654321/10/1" },

        // connection strings
        { "npg_SuperSecret", "Host=ep-cool-123.eu-central-1.aws.neon.tech;Database=neondb;Username=app_user;Password=npg_SuperSecret;SSL Mode=Require" },
        { "ep-cool-123.eu-central-1.aws.neon.tech", "Host=ep-cool-123.eu-central-1.aws.neon.tech;Database=neondb;Username=app_user;Password=x" },
        { "app_user", "Server=db.internal;Port=5432;User Id=app_user;Pwd=hidden" },
        { "npg_UrlSecret", "failed to open postgresql://app_user:npg_UrlSecret@ep-cool.neon.tech/neondb?sslmode=require" },
        { "Secret-In-A-Setting", "connectionString=Secret-In-A-Setting" },

        // email addresses
        { "ada.lovelace@example.test", "sign-in for ada.lovelace@example.test failed" },

        // where the database is, as a driver words a failure to reach it
        { "ep-quiet-dawn-123", "Failed to connect to ep-quiet-dawn-123.eu-central-1.aws.neon.tech:5432" },
        { "10.20.30.40", "Failed to connect to 10.20.30.40:5432" },

        // where the database is, as EF Core words a connection error
        { "ep-quiet-dawn-123", "An error occurred using the connection to database '\"neondb\"' on server '\"tcp://ep-quiet-dawn-123.eu-central-1.aws.neon.tech:5432\"'." },
        { "neondb", "An error occurred using the connection to database '\"neondb\"' on server '\"tcp://ep-quiet-dawn-123.example:5432\"'." },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_secret_in_text_does_not_survive(string secret, string text)
    {
        var cleaned = LogRedactor.Redact(text);

        Assert.DoesNotContain(secret, cleaned, StringComparison.Ordinal);
        Assert.Contains("redacted", cleaned, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET probe/ip responded 200 in 1.2 ms (correlation 0HNF4U5T3K2V0:00000001, user 3f2504e0-4f89-11d3-9a0c-0305e82c3301)")]
    [InlineData("Sign-in locked an account until 2026-10-12T17:44:40Z")]
    [InlineData("Brought 42 earlier audit rows into the hash chain.")]
    [InlineData("Writing the audit anchor for sequence 7 failed. It will be tried again with the next entry.")]
    public void Ordinary_text_is_left_exactly_as_it_was(string text)
    {
        Assert.Equal(text, LogRedactor.Redact(text));
    }

    // --- Through the sink, as the application's logger is built ------------------------------------------------

    private sealed class Capture : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        /// <summary>Everything a log reader would see of one event, as one string.</summary>
        public static string Text(LogEvent logEvent)
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            new Serilog.Formatting.Compact.RenderedCompactJsonFormatter().Format(logEvent, writer);

            return writer.ToString();
        }

        public string AllText => string.Join('\n', Events.Select(Text));
    }

    private static (Serilog.ILogger Logger, Capture Output) CreateLogger(bool isDevelopment = false)
    {
        var output = new Capture();
        var logger = LoggingSetup.Configure(new LoggerConfiguration(), isDevelopment, output).CreateLogger();

        return (logger, output);
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_secret_written_into_the_message_is_hidden(string secret, string text)
    {
        var (logger, output) = CreateLogger();

        logger.Warning(text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));

        Assert.DoesNotContain(secret, output.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_secret_passed_as_a_message_argument_is_hidden(string secret, string text)
    {
        var (logger, output) = CreateLogger();

        logger.Warning("Something happened: {Detail}", text);

        Assert.DoesNotContain(secret, output.AllText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Password", "plain-password-value")]
    [InlineData("password", "plain-password-value")]
    [InlineData("Token", "plain-token-value")]
    [InlineData("AccessToken", "plain-token-value")]
    [InlineData("Code", "654321")]
    [InlineData("RecoveryCode", "RECOV-ERYC-ODE1")]
    [InlineData("TwoFactorCode", "123123")]
    [InlineData("Cookie", "plain-cookie-value")]
    [InlineData("Nrc", "999999/99/9")]
    [InlineData("Passport", "ZN1234567")]
    [InlineData("ConnectionString", "plain-connection-value")]
    [InlineData("Authorization", "plain-authorization-value")]
    [InlineData("ApiKey", "plain-key-value")]
    [InlineData("Server", "tcp://plain-server-value:5432")]
    [InlineData("Database", "plain-database-value")]
    [InlineData("Host", "plain-host-value")]
    [InlineData("Secret", "plain-secret-value")]
    public void A_property_with_a_secret_name_never_shows_its_value_whatever_the_value_looks_like(string name, string value)
    {
        var (logger, output) = CreateLogger();

        using (LogContext.PushProperty(name, value))
        {
            logger.ForContext(name, value).Write(LogEventLevel.Warning, "An event with a {" + name + "} property", value);
        }

        Assert.DoesNotContain(value, output.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_inside_an_object_or_a_list_is_hidden()
    {
        var (logger, output) = CreateLogger();
        var request = new { Email = "a@b.c", Password = "nested-password-value", Inner = new { Token = "nested-token-value", Items = new[] { "list-item-123456/78/9" } } };

        logger.Warning("Request {@Request}", request);

        Assert.DoesNotContain("nested-password-value", output.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-token-value", output.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("123456/78/9", output.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("a@b.c", output.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_an_exception_message_or_its_inner_exception_or_its_stack_is_hidden()
    {
        var (logger, output) = CreateLogger();
        var inner = new InvalidOperationException("could not connect: Host=secret-host.neon.tech;Username=app;Password=inner-secret-pw");
        var outer = new InvalidOperationException("outer failed for 123456/78/9 with token=outer-token-value", inner);

        logger.Error(outer, "The thing failed");

        var text = output.AllText;
        Assert.DoesNotContain("inner-secret-pw", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host.neon.tech", text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456/78/9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("outer-token-value", text, StringComparison.Ordinal);

        // The exception is still there for the person reading the log: its type and where it came from.
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("The thing failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_level_the_time_and_ordinary_properties_are_kept()
    {
        var (logger, output) = CreateLogger();

        logger.Error("{Method} {Route} responded {Status}", "GET", "api/x", 500);

        var logEvent = Assert.Single(output.Events);
        Assert.Equal(LogEventLevel.Error, logEvent.Level);
        Assert.Equal("\"GET\" \"api/x\" responded 500", logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("\"api/x\"", logEvent.Properties["Route"].ToString());
        Assert.Equal("500", logEvent.Properties["Status"].ToString());
    }

    // --- EF Core's command text ---------------------------------------------------------------------------------

    private const string CommandCategory = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void Outside_development_ef_core_commands_are_logged_only_at_warning_and_above()
    {
        var (logger, output) = CreateLogger(isDevelopment: false);
        var commands = logger.ForContext(Constants.SourceContextPropertyName, CommandCategory);

        commands.Information("Executed DbCommand SELECT * FROM users WHERE id = @p0");
        commands.Debug("a debug line");
        commands.Warning("A command took too long");

        var logEvent = Assert.Single(output.Events);
        Assert.Equal(LogEventLevel.Warning, logEvent.Level);
    }

    [Fact]
    public void In_development_ef_core_commands_are_logged_at_information()
    {
        var (logger, output) = CreateLogger(isDevelopment: true);

        logger.ForContext(Constants.SourceContextPropertyName, CommandCategory).Information("Executed DbCommand");

        Assert.Single(output.Events);
    }

    [Fact]
    public void Other_ef_core_information_and_framework_noise_is_not_logged_in_any_environment()
    {
        foreach (var development in new[] { true, false })
        {
            var (logger, output) = CreateLogger(development);

            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.EntityFrameworkCore.Infrastructure").Information("Entity Framework Core started");
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.AspNetCore.Hosting.Diagnostics").Information("Request starting");
            logger.ForContext(Constants.SourceContextPropertyName, "System.Net.Http.HttpClient").Information("Sending request");

            Assert.Empty(output.Events);
        }
    }
}
