using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Authorization;

namespace CaseRegister.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>
    /// A signed-in user, whether or not they have passed two-factor yet. Used only where that is the
    /// point: enrolling in two-factor, signing out, and asking who the session belongs to.
    /// </summary>
    public const string SignedIn = "SignedIn";

    public static void Configure(AuthorizationOptions options)
    {
        // Deny by default: a signed-in user who has completed two-factor (amr "mfa"). Endpoints with no
        // attribute get this (the fallback policy) and so does a bare [Authorize] (the default policy).
        var fullySignedIn = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor)
            .Build();

        options.FallbackPolicy = fullySignedIn;
        options.DefaultPolicy = fullySignedIn;

        // Anything weaker has to be asked for by name.
        options.AddPolicy(SignedIn, policy => policy.RequireAuthenticatedUser());
    }
}
