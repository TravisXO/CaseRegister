using System.Security.Claims;
using CaseRegister.Application.Auditing;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CaseRegister.Api.Security;

/// <summary>
/// Put on an admin list: a successful view is written to the audit log, once for each person, list and filter in any ten minutes. The filter is
/// the query string, so a different search is a different view. If the entry cannot be written the response is an error and nothing is shown.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AuditListViewAttribute(string view) : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new AuditListViewFilter(view, serviceProvider.GetRequiredService<IAuditReadLog>());
}

internal sealed class AuditListViewFilter(string view, IAuditReadLog readLog) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var status = (executed.Result as Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult)?.StatusCode ?? 200;
        if (executed.Exception is not null || status is < 200 or >= 300 || !Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return;
        }

        var filter = string.Join('&', context.HttpContext.Request.Query.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

        await readLog.NoteViewAsync(userId, view, filter, context.HttpContext.Connection.RemoteIpAddress?.ToString(), context.HttpContext.RequestAborted);
    }
}
