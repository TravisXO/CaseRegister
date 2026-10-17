using CaseRegister.Api.Contracts;
using CaseRegister.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CaseRegister.Api.Controllers;

[ApiController]
[Route("api/deployment")]
public sealed class DeploymentController(IOptions<DeploymentOptions> options) : ControllerBase
{
    /// <summary>
    /// What kind of deployment this is, so the pages can say it before anyone has signed in (the demo notice on every page is driven by this).
    /// Public on purpose: it holds nothing about anyone.
    /// </summary>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType<DeploymentResponse>(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(new DeploymentResponse(options.Value.Mode ?? string.Empty));
}
