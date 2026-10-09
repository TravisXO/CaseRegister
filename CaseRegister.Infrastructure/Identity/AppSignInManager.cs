using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Identity's own sign-out also signs out of the external-login and "remember this device" schemes.
/// Neither exists in this system (no external logins, no two-factor remember-me), and asking for a
/// scheme that is not registered throws. The security stamp check signs a session out through this
/// same method when it rejects it, so the stock behaviour would turn every ended session into a
/// server error. This version signs out of the two schemes that do exist.
/// </summary>
internal sealed class AppSignInManager(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task SignOutAsync()
    {
        await Context.SignOutAsync(IdentityConstants.ApplicationScheme);
        await Context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
    }
}
