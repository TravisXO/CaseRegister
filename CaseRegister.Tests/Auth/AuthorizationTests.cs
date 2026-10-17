using System.Net;
using CaseRegister.Application.Auth;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Auth;

/// <summary>Deny by default, with two-factor: the fallback policy, the default policy, and the one named exception.</summary>
[Collection(PostgresTestGroup.Name)]
public class AuthorizationTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    // --- The policies themselves ---------------------------------------------------------------

    [Fact]
    public async Task The_fallback_and_default_policies_both_need_a_signed_in_user_who_has_completed_two_factor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var options = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        foreach (var policy in new[] { options.FallbackPolicy!, options.DefaultPolicy })
        {
            Assert.Contains(policy.Requirements, requirement => requirement is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);

            var claim = Assert.Single(policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.ClaimsAuthorizationRequirement>());
            Assert.Equal("amr", claim.ClaimType);
            Assert.Equal(["mfa"], claim.AllowedValues);
        }
    }

    [Fact]
    public async Task The_only_weaker_policy_is_the_named_SignedIn_one_and_it_needs_no_two_factor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var options = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value;

        var policy = options.GetPolicy("SignedIn")!;

        Assert.Single(policy.Requirements);
        Assert.IsType<Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement>(policy.Requirements.Single());
    }

    // --- Behaviour of the three kinds of session ----------------------------------------------

    [Theory]
    [InlineData("/probe/unannotated")] // no attribute: the fallback policy
    [InlineData("/probe/members-only")] // a bare [Authorize]: the default policy
    public async Task Without_a_session_endpoints_that_name_no_policy_are_401_with_a_password_only_session_403_and_with_two_factor_200(string path)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var passwordOnly = await factory.CreateUserAsync("password.only@example.test", twoFactor: false);
        var full = await factory.CreateUserAsync("full@example.test");

        var anonymous = factory.CreateApiClient();
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(factory, passwordOnly);
        var fullyIn = factory.CreateApiClient();
        await fullyIn.SignInWithTwoFactorAsync(factory, full);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fullyIn.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_named_SignedIn_policy_lets_in_a_password_only_session_and_a_full_one_but_not_anonymous()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var passwordOnly = await factory.CreateUserAsync("password.only@example.test", twoFactor: false);
        var full = await factory.CreateUserAsync("full@example.test");
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(factory, passwordOnly);
        var fullyIn = factory.CreateApiClient();
        await fullyIn.SignInWithTwoFactorAsync(factory, full);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().GetAsync("/probe/signed-in-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await restricted.GetAsync("/probe/signed-in-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fullyIn.GetAsync("/probe/signed-in-only")).StatusCode);
    }

    [Fact]
    public async Task A_member_who_has_not_finished_the_code_step_is_not_signed_in_at_all()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/signed-in-only")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/unannotated")).StatusCode);
    }

    [Fact]
    public async Task A_session_claiming_nothing_about_how_it_signed_in_gets_no_special_treatment()
    {
        // Full sign-ins carry "mfa", enrolment-only ones carry "pwd". Neither is a way to skip the other.
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var passwordOnly = await factory.CreateUserAsync(Email, twoFactor: false);
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(factory, passwordOnly);

        var session = await (await restricted.GetAsync("/api/auth/session")).Content.ReadAsStringAsync();

        Assert.Contains("\"twoFactorComplete\":false", session, StringComparison.Ordinal);
    }

    // --- Every real endpoint, accounted for -----------------------------------------------------

    private sealed record EndpointRule(string Method, string Route, string Rule);

    private static string RuleOf(RouteEndpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return "anonymous";
        }

        var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).FirstOrDefault(p => p is not null);

        // No attribute, or a bare [Authorize]: the fallback or default policy, both of which need two-factor.
        return policy ?? "two-factor";
    }

    [Fact]
    public async Task Every_real_endpoint_is_either_deliberately_open_deliberately_half_open_or_needs_two_factor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("api/", StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(method => new EndpointRule(method, endpoint.RoutePattern.RawText!, RuleOf(endpoint))))
            .OrderBy(rule => rule.Route).ThenBy(rule => rule.Method)
            .ToList();

        var expected = new List<EndpointRule>
        {
            new("POST", "api/auth/2fa/enrol", "SignedIn"),
            new("POST", "api/auth/2fa/enrol/confirm", "SignedIn"),
            new("POST", "api/auth/2fa/recovery-codes", "two-factor"),
            new("POST", "api/auth/2fa/verify", "anonymous"),
            new("POST", "api/auth/change-password", "two-factor"),
            new("GET", "api/auth/csrf", "anonymous"),
            new("GET", "api/auth/session", "SignedIn"),
            new("GET", "api/auth/sessions", "two-factor"),
            new("DELETE", "api/auth/sessions/{id:guid}", "two-factor"),
            new("POST", "api/auth/sign-in", "anonymous"),
            new("POST", "api/auth/sign-out", "SignedIn"),
            new("POST", "api/auth/sign-up", "anonymous"),
            new("POST", "api/auth/staff-setup/start", "anonymous"),
            new("POST", "api/auth/staff-setup/complete", "anonymous"),
            new("GET", "api/admin/applications", "ViewApplications"),
            new("GET", "api/audit/entries", "ReadAuditLog"),
            new("POST", "api/audit/verify", "VerifyAuditLog"),

            // Admin actions: each has the named policy for what it does, and nothing is left to a bare [Authorize].
            new("GET", "api/admin/pending-actions", "ViewPendingActions"),
            new("GET", "api/admin/pending-actions/{id:guid}", "ViewPendingActions"),
            new("POST", "api/admin/pending-actions", "ProposeAdminActions"),
            new("POST", "api/admin/pending-actions/{id:guid}/confirm", "ConfirmAdminActions"),
            new("POST", "api/admin/pending-actions/{id:guid}/reject", "ConfirmAdminActions"),
            new("POST", "api/admin/pending-actions/{id:guid}/withdraw", "ProposeAdminActions"),
            new("POST", "api/admin/users/{id:guid}/suspend", "RestrictAccess"),
            new("POST", "api/admin/users/{id:guid}/end-sessions", "RestrictAccess"),
            new("POST", "api/admin/agencies/{id:guid}/suspend", "RestrictAccess"),

            new("GET", "api/me", "two-factor"),
            new("GET", "api/me/practising-certificates", "two-factor"),
            new("POST", "api/me/practising-certificates", "two-factor"),

            // Everyone signed in with two factors has an inbox; each notification is checked against its owner.
            new("GET", "api/notifications", "two-factor"),
            new("POST", "api/notifications/{id:guid}/acknowledge", "two-factor"),
            new("POST", "api/notifications/{id:guid}/read", "two-factor"),
            new("POST", "api/notifications/read-all", "two-factor"),
        }.OrderBy(rule => rule.Route).ThenBy(rule => rule.Method).ToList();

        Assert.Equal(expected, endpoints);

        // The only endpoints open to anyone are those that have to be: a token, the two sign-in steps, applying, and the two steps of staff setup.
        Assert.Equal(
            ["api/auth/2fa/verify", "api/auth/csrf", "api/auth/sign-in", "api/auth/sign-up", "api/auth/staff-setup/complete", "api/auth/staff-setup/start"],
            endpoints.Where(rule => rule.Rule == "anonymous").Select(rule => rule.Route).Order(StringComparer.Ordinal));
    }
}
