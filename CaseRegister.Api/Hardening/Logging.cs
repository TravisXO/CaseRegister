using System.Diagnostics;
using System.Security.Claims;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace CaseRegister.Api.Hardening;

/// <summary>Tests register one to see what would have been written to the console, after redaction.</summary>
public sealed class LogOutputOverride(ILogEventSink sink)
{
    public ILogEventSink Sink { get; } = sink;
}

public static class LoggingSetup
{
    /// <summary>
    /// Console only (Render collects it), as one JSON object per line. Everything passes through the redaction step on its
    /// way out. EF Core's command text is logged only in Development, and sensitive data logging is never switched on.
    /// </summary>
    public static LoggerConfiguration Configure(LoggerConfiguration configuration, bool isDevelopment, ILogEventSink? output = null)
    {
        configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            .Enrich.FromLogContext();

        if (isDevelopment)
        {
            configuration.MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Information);
        }

        var console = output ?? new LoggerConfiguration().WriteTo.Console(new RenderedCompactJsonFormatter()).CreateLogger();

        return configuration.WriteTo.Sink(new RedactingSink(console));
    }
}

/// <summary>
/// One line per request when it ends: method, route template (never the raw path), status, duration, correlation id and
/// user id. The user id is the account's id; the email address is never used.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = time.GetTimestamp();

        // Taken now: the exception handler clears the endpoint when it runs.
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(no route)";
        try
        {
            await next(context);
        }
        catch
        {
            // The request ended in an exception nothing handled: it will be a 500.
            Log(context, route, StatusCodes.Status500InternalServerError, started);
            throw;
        }

        Log(context, route, context.Response.StatusCode, started);
    }

    private void Log(HttpContext context, string route, int status, long started)
    {
        var elapsed = time.GetElapsedTime(started).TotalMilliseconds;
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var level = status >= 500 ? LogLevel.Error : LogLevel.Information;

        var rounded = Math.Round(elapsed, 1);
        var correlationId = ProblemResponses.TraceIdOf(context);

        LogRequest(logger, level, context.Request.Method, route, status, rounded, correlationId, userId);
    }

    [LoggerMessage(Message = "{Method} {Route} responded {Status} in {ElapsedMs} ms (correlation {CorrelationId}, user {UserId})")]
    private static partial void LogRequest(Microsoft.Extensions.Logging.ILogger logger, LogLevel level, string method, string route, int status, double elapsedMs, string correlationId, string userId);
}
