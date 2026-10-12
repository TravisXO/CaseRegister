using System.Security.Claims;
using CaseRegister.Application.Auditing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http;

namespace CaseRegister.Api.Security;

/// <summary>
/// Put on an action that shows a sensitive record (an FIC report, a case, a person's details) and every successful
/// view is written to the audit log: who, which kind of record, which id, from where. It is written before the
/// response goes out, and if the entry cannot be written the response is an error, so nothing is shown unrecorded.
/// </summary>
/// <param name="targetType">What kind of record this action shows, for example "FicReport".</param>
/// <param name="routeKey">The route value that holds the record's id.</param>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class AuditViewAttribute(string targetType, string routeKey = "id") : Attribute, IFilterFactory
{
    public string TargetType { get; } = targetType;

    public string RouteKey { get; } = routeKey;

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new AuditViewFilter(TargetType, RouteKey, serviceProvider.GetRequiredService<IAuditLog>());
}

internal sealed class AuditViewFilter(string targetType, string routeKey, IAuditLog audit) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();

        if (executed.Exception is not null || !Succeeded(executed.Result))
        {
            return; // nothing was shown, so there is nothing to record
        }

        var userId = Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var recordId = context.RouteData.Values.TryGetValue(routeKey, out var value) ? value?.ToString() : null;

        // If this throws the response becomes an error and the record is not shown.
        await audit.WriteAsync(
            new AuditEntry(
                AuditAction.RecordViewed,
                userId,
                Subject: recordId,
                IpAddress: context.HttpContext.Connection.RemoteIpAddress?.ToString(),
                TargetType: targetType,
                Detail: $"viewed with {context.HttpContext.Request.Method} {context.ActionDescriptor.AttributeRouteInfo?.Template}"),
            context.HttpContext.RequestAborted);
    }

    private static bool Succeeded(IActionResult? result) => result switch
    {
        null => false,
        Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult { StatusCode: { } status } => status is >= 200 and < 300,
        _ => true,
    };
}
