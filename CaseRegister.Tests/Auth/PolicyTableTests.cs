using System.Security.Claims;
using CaseRegister.Api.Security.Authorization;
using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>
/// Every role against every policy, using real accounts and the real authorisation service, compared
/// with the independent table. Any change to the matrix shows up here as a failing test.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public class PolicyTableTests(PostgresFixture postgres)
{
    private static readonly Role[] _allRoles = Enum.GetValues<Role>();

    private sealed record People(ApiFactory Factory, Dictionary<Role, ApplicationUser> ByRole);

    /// <summary>One person per role. Agents get a current, verified certificate; the association's staff have none and need none.</summary>
    private static async Task<People> PeopleAsync(ApiFactory factory, bool currentCertificate = true)
    {
        var agency = await factory.CreateAgencyAsync();
        var byRole = new Dictionary<Role, ApplicationUser>
        {
            [Role.RegisteredAgent] = await factory.CreateMemberAsync("agent@example.test", agency, [Role.RegisteredAgent], currentCertificate),
            [Role.AgencyComplianceOfficer] = await factory.CreateMemberAsync("officer@example.test", agency, [Role.RegisteredAgent, Role.AgencyComplianceOfficer], currentCertificate),
            [Role.CaseReviewer] = await factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer),
            [Role.Admin] = await factory.CreateStaffAsync("admin@example.test", Role.Admin),
        };

        return new People(factory, byRole);
    }

    private static async Task<ClaimsPrincipal> PrincipalOfAsync(IServiceProvider services, ApplicationUser user, bool twoFactorDone = true)
    {
        var principal = await services.GetRequiredService<SignInManager<ApplicationUser>>().CreateUserPrincipalAsync(user);

        // How the session records whether the second factor has passed.
        principal.Identities.First().AddClaim(new Claim("amr", twoFactorDone ? "mfa" : "pwd"));

        return principal;
    }

    private static async Task<bool> AllowedAsync(IServiceProvider services, ClaimsPrincipal principal, string policy) =>
        (await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy)).Succeeded;

    /// <summary>"RegisteredAgent can ReadFicReports" for every cell, as one readable list.</summary>
    private static async Task<List<string>> TableAsync(People people, bool twoFactorDone = true)
    {
        using var scope = people.Factory.Services.CreateScope();
        var cells = new List<string>();

        foreach (var role in _allRoles)
        {
            var principal = await PrincipalOfAsync(scope.ServiceProvider, people.ByRole[role], twoFactorDone);

            foreach (var name in ExpectedMatrix.PolicyNames)
            {
                var allowed = await AllowedAsync(scope.ServiceProvider, principal, name);
                cells.Add($"{role} {(allowed ? "can" : "cannot")} {name}");
            }
        }

        return cells;
    }

    private static List<string> Expected(Func<Role, Capability, bool> allowed) =>
    [
        .. _allRoles.SelectMany(role => Enum.GetValues<Capability>().Select(capability =>
            $"{role} {(allowed(role, capability) ? "can" : "cannot")} {capability}")),
    ];

    // --- The table --------------------------------------------------------------------------------

    [Fact]
    public async Task Every_role_against_every_policy_matches_the_specification()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var people = await PeopleAsync(factory);

        var actual = await TableAsync(people);

        Assert.Equal(60, actual.Count); // 4 roles by 15 policies
        Assert.Equal(Expected((role, capability) => ExpectedMatrix.Allows(capability, role)), actual);
    }

    [Fact]
    public async Task Without_two_factor_every_policy_refuses_every_role()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var people = await PeopleAsync(factory);

        var actual = await TableAsync(people, twoFactorDone: false);

        Assert.Equal(Expected((_, _) => false), actual);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_by_every_policy()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();

        foreach (var name in ExpectedMatrix.PolicyNames)
        {
            Assert.False(await AllowedAsync(scope.ServiceProvider, new ClaimsPrincipal(new ClaimsIdentity()), name), name);
        }
    }

    [Fact]
    public async Task A_member_with_no_role_yet_can_do_nothing_whatever_certificate_they_hold()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var pending = await factory.CreateMemberAsync("pending@example.test", agency, [], currentCertificate: true);
        using var scope = factory.Services.CreateScope();
        var principal = await PrincipalOfAsync(scope.ServiceProvider, pending);

        foreach (var name in ExpectedMatrix.PolicyNames)
        {
            Assert.False(await AllowedAsync(scope.ServiceProvider, principal, name), name);
        }
    }

    // --- The practising certificate requirement ---------------------------------------------------

    [Fact]
    public async Task Without_a_current_certificate_an_agent_is_refused_by_every_policy_but_staff_are_unaffected()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var people = await PeopleAsync(factory, currentCertificate: false);

        var actual = await TableAsync(people);

        // Agents lose everything. Reviewers and admins have no certificate and keep their access.
        Assert.Equal(
            Expected((role, capability) => ExpectedMatrix.Allows(capability, role) && role.IsStaffRole()),
            actual);
    }

    public static TheoryData<string> CertificateProblems() => ["unverified", "expired last year", "starts next year"];

    [Theory]
    [MemberData(nameof(CertificateProblems))]
    public async Task A_certificate_that_is_not_both_verified_and_in_date_does_not_count(string problem)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var officer = await factory.CreateMemberAsync("officer@example.test", agency, [Role.RegisteredAgent, Role.AgencyComplianceOfficer], currentCertificate: false);
        var year = factory.Today.Year;
        _ = problem switch
        {
            "unverified" => await factory.AddCertificateAsync(officer, year, verified: false),
            "expired last year" => await factory.AddCertificateAsync(officer, year - 1),
            _ => await factory.AddCertificateAsync(officer, year + 1),
        };
        using var scope = factory.Services.CreateScope();
        var principal = await PrincipalOfAsync(scope.ServiceProvider, officer);

        foreach (var capability in PermissionMatrix.Capabilities.Where(capability => ExpectedMatrix.Allows(capability, Role.AgencyComplianceOfficer)))
        {
            Assert.False(await AllowedAsync(scope.ServiceProvider, principal, capability.ToString()), capability.ToString());
        }
    }

    [Fact]
    public async Task Last_years_certificate_and_this_years_together_give_access_because_this_years_is_current()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var agent = await factory.CreateMemberAsync("agent@example.test", agency, [Role.RegisteredAgent], currentCertificate: false);
        await factory.AddCertificateAsync(agent, factory.Today.Year - 1);
        await factory.AddCertificateAsync(agent, factory.Today.Year);
        using var scope = factory.Services.CreateScope();

        Assert.True(await AllowedAsync(scope.ServiceProvider, await PrincipalOfAsync(scope.ServiceProvider, agent), "RunPreDealCheck"));
    }

    [Fact]
    public async Task The_membership_class_decides_nothing_only_the_certificate_does()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var members = new Dictionary<MembershipClass, (ApplicationUser WithCertificate, ApplicationUser Without)>();
        foreach (var membershipClass in Enum.GetValues<MembershipClass>())
        {
            members[membershipClass] = (
                await factory.CreateMemberAsync($"{membershipClass}.with@example.test", agency, [Role.RegisteredAgent], currentCertificate: true, membershipClass),
                await factory.CreateMemberAsync($"{membershipClass}.without@example.test", agency, [Role.RegisteredAgent], currentCertificate: false, membershipClass));
        }

        using var scope = factory.Services.CreateScope();
        foreach (var (membershipClass, pair) in members)
        {
            // Honorary or Full, Student or Associate: with a current certificate they can; without, they cannot.
            Assert.True(await AllowedAsync(scope.ServiceProvider, await PrincipalOfAsync(scope.ServiceProvider, pair.WithCertificate), "RunPreDealCheck"), $"{membershipClass} with a certificate");
            Assert.False(await AllowedAsync(scope.ServiceProvider, await PrincipalOfAsync(scope.ServiceProvider, pair.Without), "RunPreDealCheck"), $"{membershipClass} without one");
        }
    }

    // --- How the policies are built ---------------------------------------------------------------

    [Fact]
    public async Task Every_action_has_exactly_one_policy_of_the_same_name_and_each_needs_two_factor()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in ExpectedMatrix.PolicyNames)
        {
            var policy = await provider.GetPolicyAsync(name);

            Assert.NotNull(policy);
            var claim = Assert.Single(policy.Requirements.OfType<ClaimsAuthorizationRequirement>());
            Assert.Equal("amr", claim.ClaimType);
            Assert.Equal(["mfa"], claim.AllowedValues);
            Assert.Contains(policy.Requirements, requirement => requirement is DenyAnonymousAuthorizationRequirement);
            Assert.Single(policy.Requirements.OfType<CapabilityRequirement>());
        }
    }

    [Fact]
    public async Task The_certificate_requirement_is_on_exactly_the_policies_an_agent_role_can_use()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var withRequirement = new List<string>();

        foreach (var name in ExpectedMatrix.PolicyNames)
        {
            var policy = (await provider.GetPolicyAsync(name))!;
            if (policy.Requirements.OfType<HasCurrentPractisingCertificateRequirement>().Any())
            {
                withRequirement.Add(name);
            }
        }

        var agentCanUse = ExpectedMatrix.PolicyNames
            .Where(name => ExpectedMatrix.Allows(Enum.Parse<Capability>(name), Role.RegisteredAgent) || ExpectedMatrix.Allows(Enum.Parse<Capability>(name), Role.AgencyComplianceOfficer))
            .ToList();

        Assert.Equal(agentCanUse.Order(StringComparer.Ordinal), withRequirement.Order(StringComparer.Ordinal));
        Assert.Equal(9, withRequirement.Count);
    }

    [Fact]
    public async Task The_active_agency_requirement_sits_next_to_the_certificate_requirement_on_exactly_the_same_policies()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var name in ExpectedMatrix.PolicyNames)
        {
            var policy = (await provider.GetPolicyAsync(name))!;

            Assert.Equal(
                policy.Requirements.OfType<HasCurrentPractisingCertificateRequirement>().Any(),
                policy.Requirements.OfType<ActiveAgencyRequirement>().Any());
        }

        // And the admin actions, which no agent can use, have neither.
        foreach (var name in new[] { "ViewPendingActions", "ProposeAdminActions", "ConfirmAdminActions", "RestrictAccess" })
        {
            Assert.DoesNotContain((await provider.GetPolicyAsync(name))!.Requirements, requirement => requirement is ActiveAgencyRequirement);
        }
    }

    // --- Through the real session -----------------------------------------------------------------

    [Fact]
    public async Task A_signed_in_session_carries_the_roles_so_the_policies_work_over_http()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var people = await PeopleAsync(factory);
        var client = factory.CreateApiClient();
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, people.ByRole[Role.CaseReviewer])).StatusCode);

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/probe/can/DecideConductCases")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/probe/can/ViewUpheldCases")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/ApproveAgents")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/ReadFicReports")).StatusCode);
    }
}
