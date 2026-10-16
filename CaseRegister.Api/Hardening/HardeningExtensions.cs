using System.Globalization;
using System.Text.Json.Serialization;
using CaseRegister.Api.Security;
using CaseRegister.Infrastructure.Health;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using Serilog;

namespace CaseRegister.Api.Hardening;

public static class HardeningExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string PortVariable = "PORT";

    /// <summary>Everything outside the controllers: logging, errors, limits, headers and health checks.</summary>
    public static WebApplicationBuilder AddApiHardening(this WebApplicationBuilder builder)
    {
        var isDevelopment = builder.Environment.IsDevelopment();

        // Render says which port to listen on.
        if (ListenUrlFor(Environment.GetEnvironmentVariable(PortVariable)) is { } listenUrl)
        {
            builder.WebHost.UseUrls(listenUrl);
        }

        builder.WebHost.ConfigureKestrel(RequestLimits.ConfigureKestrel);

        // Serilog is the only logger: the console provider the host adds by default would write the same events a second time, unredacted.
        builder.Logging.ClearProviders();

        // Each host keeps its own logger. Left to its default, Serilog also sets the static Log.Logger, which every host in the same process
        // shares, and closes it when any one of them stops: with several hosts in one process (the tests) one stopping silences the rest.
        // Nothing here uses the static logger.
        builder.Host.UseSerilog(
            (_, services, configuration) => LoggingSetup.Configure(configuration, isDevelopment, services.GetService<LogOutputOverride>()?.Sink),
            preserveStaticLogger: true,
            writeToProviders: true);

        var services = builder.Services;

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context => ProblemResponses.Customise(context, isDevelopment));
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.PostConfigure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = context => InvalidModelResponse(context, isDevelopment));

        services.AddRequestTimeouts(options => options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = RequestLimits.RequestTimeout,
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
        });

        // 308 keeps the method and body. The port is stated because behind a proxy the app cannot work it out.
        services.AddHttpsRedirection(options =>
        {
            options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
            options.HttpsPort = 443;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddReadinessChecks();

        // Only the proxy may reach the API.
        services.AddProxyGate();

        return builder;
    }

    /// <summary>
    /// The address to listen on for the PORT the platform gives us, or null when there is none (the image default applies).
    /// A bad value stops startup, naming the variable but never showing what it held.
    /// </summary>
    public static string? ListenUrlFor(string? port)
    {
        if (string.IsNullOrEmpty(port))
        {
            return null;
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{PortVariable} must be a port number from 1 to 65535.");
        }

        return $"http://0.0.0.0:{number.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>JSON options: enums by name, unknown properties refused, a maximum depth.</summary>
    public static IMvcBuilder AddStrictJson(this IMvcBuilder mvc) =>
        mvc.AddJsonOptions(options =>
        {
            // Enums travel as their names ("AssignRole"), never as numbers that mean something different if the list is reordered.
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow; // Default: Skip.
            options.JsonSerializerOptions.MaxDepth = RequestLimits.MaxJsonDepth; // Default: 64.
            options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict; // Default: a number may arrive as text.
        });

    /// <summary>The middleware in the order that matters. Call it first, before anything else is added to the pipeline.</summary>
    public static WebApplication UseApiHardening(this WebApplication app)
    {
        // Must be first, so everything after it sees the real client address and scheme.
        app.UseForwardedHeaders();

        // Outside the exception handler, so an unhandled exception still gets its line, with the 500 it became.
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseExceptionHandler();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        // After the headers and the exception handler, so a refusal carries both. Before everything else: a request that did not come
        // through the proxy gets nothing, and one that did tells us the visitor's address for the limits and the audit log below.
        app.UseMiddleware<ProxyGateMiddleware>();

        // A health check from the platform arrives over plain HTTP, without the forwarded scheme, and must not be redirected.
        app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());

        // An empty 4xx or 5xx response becomes a ProblemDetails.
        app.UseStatusCodePages();

        app.UseMiddleware<RequestBodyLimitMiddleware>();
        app.UseRequestTimeouts();

        return app;
    }

    /// <summary>The health endpoints (anonymous, a status word and nothing else) and the catch-all that hides which routes exist.</summary>
    public static WebApplication MapOperationalEndpoints(this WebApplication app)
    {
        // Alive: nothing is checked, so the platform's frequent calls never wake the database.
        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteStatusWord })
            .AllowAnonymous()
            .DisableRateLimiting();

        // Ready: the database answers. The answer is reused for a few seconds.
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthChecksSetup.ReadyTag), ResponseWriter = WriteStatusWord })
            .AllowAnonymous()
            .DisableRateLimiting();

        // Any other address needs a signed-in user like everything else: an anonymous caller gets 401 whether the route exists or not.
        // Someone signed in gets a plain 404.
        app.MapFallback(() => Results.NotFound());

        return app;
    }

    /// <summary>Registers the API description. It is only ever served in Development (see <see cref="MapApiReference"/>).</summary>
    public static IServiceCollection AddApiDescription(this IServiceCollection services)
    {
        // The description is built from these JSON options, not the MVC ones, so they say the same things: enums are their names and numbers are numbers.
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        return services.AddOpenApi();
    }

    /// <summary>The API description and its reference page. Development only: in any other environment neither is mapped at all.</summary>
    public static WebApplication MapApiReference(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi().AllowAnonymous();
            app.MapScalarApiReference(options => options.WithTitle("API reference")).AllowAnonymous();
        }

        return app;
    }

    private static Task WriteStatusWord(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "text/plain";

        return context.Response.WriteAsync(report.Status.ToString());
    }

    private static BadRequestObjectResult InvalidModelResponse(ActionContext context, bool isDevelopment)
    {
        // A body the framework could not read. Outside Development it says only that, never which property or what was sent.
        var problem = isDevelopment
            ? new ValidationProblemDetails(context.ModelState)
            : new ValidationProblemDetails(new Dictionary<string, string[]> { ["body"] = ["The request body is not valid."] });
        problem.Status = StatusCodes.Status400BadRequest;
        problem.Title = "One or more validation errors occurred.";
        ProblemResponses.AddTraceId(problem, context.HttpContext);

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }
}
