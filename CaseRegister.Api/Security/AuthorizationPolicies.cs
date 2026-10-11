using CaseRegister.Api.Security.Authorization;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace CaseRegister.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>
    /// A signed-in user, whether or not they have passed two-factor yet. Used only where that is the
    /// point: enrolling in two-factor, signing out, and asking who the session belongs to.
    /// </summary>
    public const string SignedIn = "SignedIn";

    /// <summary>The policy for an action has the action's name.</summary>
    public static string NameOf(Capability capability) => capability.ToString();

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

        // One policy per action, all generated from the permission matrix, so the matrix is the only
        // place that says who may do what.
        foreach (var capability in PermissionMatrix.Capabilities)
        {
            options.AddPolicy(NameOf(capability), policy =>
            {
                // Everything the fallback policy requires...
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor);

                // ...plus a role the matrix gives this action to...
                policy.AddRequirements(new CapabilityRequirement(capability));

                // ...and, if an agent role can do it, a current verified practising certificate and an active agency.
                if (PermissionMatrix.IncludesAgentRole(capability))
                {
                    policy.AddRequirements(new HasCurrentPractisingCertificateRequirement(), new ActiveAgencyRequirement());
                }
            });
        }
    }

    public static IServiceCollection AddAuthorizationHandlers(this IServiceCollection services)
    {
        // Scoped: the certificate check asks the database, through request-scoped services.
        services.AddScoped<IAuthorizationHandler, CapabilityHandler>();
        services.AddScoped<IAuthorizationHandler, HasCurrentPractisingCertificateHandler>();
        services.AddScoped<IAuthorizationHandler, ActiveAgencyHandler>();
        services.AddScoped<IAuthorizationHandler, OwnRecordHandler>();
        services.AddScoped<IAuthorizationHandler, SameAgencyHandler>();
        services.AddScoped<IAuthorizationHandler, CapabilityScopeHandler>();

        return services;
    }
}
