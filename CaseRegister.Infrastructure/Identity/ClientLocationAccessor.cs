using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Http;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// The place the proxy said the current request came from. It is put on the request by the proxy gate, and only after the proxy secret was
/// accepted, so it is there only for requests that really came through the Pages Function. Outside a request (a command-line run) there is none.
/// </summary>
internal sealed class ClientLocationAccessor(IHttpContextAccessor contextAccessor) : IClientLocationAccessor
{
    public ClientLocation? Current =>
        contextAccessor.HttpContext?.Items.TryGetValue(ClientLocation.ItemKey, out var value) == true ? value as ClientLocation : null;
}
