using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CaseRegister.Api.Security;

/// <summary>
/// Checks the anti-forgery token on every POST, PUT, PATCH and DELETE, for every controller, with no
/// way to forget it on a new endpoint. GET, HEAD, OPTIONS and TRACE are safe methods and are not checked.
/// (The framework's own auto-validate attribute needs the MVC view services, which an API does not have.)
/// </summary>
public sealed class AntiforgeryAuthorizationFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (IsSafeMethod(context.HttpContext.Request.Method))
        {
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            // Generic on purpose: nothing about why it failed helps a forger.
            context.Result = new BadRequestObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid request",
                Detail = "The anti-forgery token is missing or invalid.",
            });
        }
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
