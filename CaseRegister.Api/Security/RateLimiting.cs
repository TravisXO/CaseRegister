using System.Globalization;
using System.Threading.RateLimiting;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace CaseRegister.Api.Security;

public static class RateLimitPolicies
{
    public const string SignInPerIp = "sign-in-per-ip";
    public const string SignUpPerIp = "sign-up-per-ip";
    public const string StaffSetupPerIp = "staff-setup-per-ip";
}

/// <summary>The sign-in limits. The defaults are the real ones; tests lower or raise them.</summary>
public sealed class RateLimitingOptions
{
    public int SignInPerIpPerMinute { get; set; } = 5;

    public int SignInPerAccountPerHour { get; set; } = 10;

    /// <summary>Sign-ups per client address per hour.</summary>
    public int SignUpPerIpPerHour { get; set; } = 3;

    public int StaffSetupPerIpPerMinute { get; set; } = 10;
}

/// <summary>
/// Limits sign-in attempts per account (per email address, whether or not the account exists, so
/// the limit itself reveals nothing). The per-IP limit is the built-in rate limiter middleware.
/// </summary>
public sealed class SignInAccountLimiter : IDisposable
{
    private const int Segments = 6;
    private readonly PartitionedRateLimiter<string> _limiter;

    public SignInAccountLimiter(IOptions<RateLimitingOptions> options)
    {
        var permitLimit = options.Value.SignInPerAccountPerHour;

        _limiter = PartitionedRateLimiter.Create<string, string>(email =>
            RateLimitPartition.GetSlidingWindowLimiter(email, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromHours(1),
                SegmentsPerWindow = Segments,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    public bool TryAcquire(string email, out TimeSpan retryAfter)
    {
        using var lease = _limiter.AttemptAcquire(EmailNormaliser.Normalise(email));
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(10);

        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Applies the per-account limit once the request body has been read and validated.</summary>
public sealed class SignInAccountRateLimitFilter(SignInAccountLimiter limiter) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        var command = context.ActionArguments.Values.OfType<SignInCommand>().FirstOrDefault();
        if (command is null)
        {
            return;
        }

        if (!limiter.TryAcquire(command.Email, out var retryAfter))
        {
            context.Result = TooManyRequests.Result(context.HttpContext, retryAfter);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>The one 429 response, used by both limits: Retry-After plus a generic problem body.</summary>
public static class TooManyRequests
{
    public static ObjectResult Result(HttpContext context, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);

        return new ObjectResult(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many attempts",
            Detail = "Too many sign-in attempts. Please wait and try again.",
        })
        {
            StatusCode = StatusCodes.Status429TooManyRequests,
        };
    }
}
