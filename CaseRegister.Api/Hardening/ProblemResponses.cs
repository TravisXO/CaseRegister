using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CaseRegister.Api.Hardening;

/// <summary>Every error leaves the API as a ProblemDetails with the same trace id the logs and the audit log carry, and nothing else that helps an attacker.</summary>
public static class ProblemResponses
{
    public const string TraceIdKey = "traceId";

    /// <summary>The id of this request: the same value the request log and the audit log call the correlation id.</summary>
    public static string TraceIdOf(HttpContext context) => context.TraceIdentifier;

    public static void AddTraceId(ProblemDetails problem, HttpContext context) => problem.Extensions[TraceIdKey] = TraceIdOf(context);

    /// <summary>
    /// The one place the client-facing text of an error is decided. Outside Development a status code has its standard
    /// title only: no exception message, no type name, no SQL and nothing the client sent.
    /// </summary>
    public static void Customise(ProblemDetailsContext context, bool isDevelopment)
    {
        var problem = context.ProblemDetails;
        AddTraceId(problem, context.HttpContext);

        if (isDevelopment)
        {
            return;
        }

        problem.Extensions.Remove("exception");
        problem.Extensions.Remove("exceptionDetails");
        problem.Instance = null;
        if (problem.Status is >= 500)
        {
            problem.Detail = null;
        }
    }
}

/// <summary>Gives every ProblemDetails an action returns (including the validation ones) the trace id.</summary>
public sealed class ProblemDetailsTraceFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problem })
        {
            ProblemResponses.AddTraceId(problem, context.HttpContext);
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}

/// <summary>
/// Turns an exception that escapes the pipeline into a ProblemDetails. A bad request the server itself refused (a body over
/// the limit, a malformed header) keeps its own 4xx status; everything else is a plain 500. The exception is logged by the
/// framework, never shown.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, IWebHostEnvironment environment) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var status = exception is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError;
        httpContext.Response.StatusCode = status;

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = TitleFor(status),

                // The reason is for a developer at their own desk. Anywhere else it is only in the logs.
                Detail = environment.IsDevelopment() ? exception.Message : null,
            },
        });
    }

    internal static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status408RequestTimeout => "Request timeout",
        StatusCodes.Status413PayloadTooLarge => "Request too large",
        StatusCodes.Status414UriTooLong => "Request address too long",
        StatusCodes.Status415UnsupportedMediaType => "Unsupported media type",
        StatusCodes.Status431RequestHeaderFieldsTooLarge => "Request headers too large",
        _ => "An error occurred while processing your request",
    };
}
