using System.Security.Claims;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Authorization;

namespace CaseRegister.Api.Security.Authorization;

/// <summary>The user holds a role the permission matrix gives this capability to.</summary>
public sealed class CapabilityRequirement(Capability capability) : IAuthorizationRequirement
{
    public Capability Capability { get; } = capability;
}

/// <summary>
/// An agent holds a current, verified practising certificate. It is on every policy an agent role can
/// use. People who are not agents (the association's staff) are not subject to it.
/// </summary>
public sealed class HasCurrentPractisingCertificateRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// An agent's agency is active. A suspended agency's members lose every agent capability, from their next
/// request. It sits next to the certificate requirement on every policy an agent role can use.
/// </summary>
public sealed class ActiveAgencyRequirement : IAuthorizationRequirement
{
}

/// <summary>The record being accessed is the user's own.</summary>
public sealed class OwnRecordRequirement : IAuthorizationRequirement
{
}

/// <summary>The record being accessed belongs to the user's agency.</summary>
public sealed class SameAgencyRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// The record is within how far the matrix lets the user's roles reach for this capability: all
/// records, their agency's, their own, or (for actions that involve no record) nothing in particular.
/// </summary>
public sealed class CapabilityScopeRequirement(Capability capability) : IAuthorizationRequirement
{
    public Capability Capability { get; } = capability;
}

/// <summary>What the handlers read from the signed-in user's claims.</summary>
internal static class UserClaims
{
    public static IReadOnlyList<Role> Roles(ClaimsPrincipal user) =>
        [.. user.FindAll(ClaimTypes.Role).Select(claim => Enum.TryParse<Role>(claim.Value, out var role) ? role : (Role?)null).OfType<Role>()];

    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static Guid? AgencyId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(AuthenticationClaims.AgencyId), out var id) ? id : null;
}

internal sealed class CapabilityHandler : AuthorizationHandler<CapabilityRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CapabilityRequirement requirement)
    {
        if (PermissionMatrix.IsAllowed(requirement.Capability, UserClaims.Roles(context.User)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class HasCurrentPractisingCertificateHandler(IPractisingCertificateService certificates)
    : AuthorizationHandler<HasCurrentPractisingCertificateRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        HasCurrentPractisingCertificateRequirement requirement)
    {
        // Not an agent: the requirement does not apply to them.
        if (!UserClaims.Roles(context.User).Any(role => role.IsAgentRole()))
        {
            context.Succeed(requirement);
            return;
        }

        // Asked of the database every time, not remembered in the session: a certificate that expires,
        // or one that is verified, counts from the next request.
        if (UserClaims.UserId(context.User) is { } userId && await certificates.HasCurrentAsync(userId))
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class ActiveAgencyHandler(IAgencyStatus agencies) : AuthorizationHandler<ActiveAgencyRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveAgencyRequirement requirement)
    {
        // Not an agent: the requirement does not apply to them.
        if (!UserClaims.Roles(context.User).Any(role => role.IsAgentRole()))
        {
            context.Succeed(requirement);
            return;
        }

        // Asked of the database every time, so a suspension counts from the very next request. No agency, no access.
        if (UserClaims.AgencyId(context.User) is { } agencyId && await agencies.IsActiveAsync(agencyId))
        {
            context.Succeed(requirement);
        }
    }
}

internal sealed class OwnRecordHandler: AuthorizationHandler<OwnRecordRequirement, object>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnRecordRequirement requirement, object resource)
    {
        if (UserClaims.UserId(context.User) is { } userId && ResourceAccess.IsOwnRecord(userId, resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class SameAgencyHandler : AuthorizationHandler<SameAgencyRequirement, object>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SameAgencyRequirement requirement, object resource)
    {
        if (ResourceAccess.IsSameAgency(UserClaims.AgencyId(context.User), resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class CapabilityScopeHandler : AuthorizationHandler<CapabilityScopeRequirement, object>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, CapabilityScopeRequirement requirement, object resource)
    {
        var scope = PermissionMatrix.WidestScope(requirement.Capability, UserClaims.Roles(context.User));

        if (scope is { } widest
            && UserClaims.UserId(context.User) is { } userId
            && ResourceAccess.Reaches(widest, userId, UserClaims.AgencyId(context.User), resource))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
