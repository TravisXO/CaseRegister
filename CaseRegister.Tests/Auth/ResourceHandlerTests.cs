using System.Security.Claims;
using CaseRegister.Api.Security.Authorization;
using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Accounts;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

public sealed class ResourceHandlerFixture : IDisposable
{
    public ApiFactory Factory { get; } = ApiFactory.WithoutDatabase();

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// The "own record" and "same agency" handlers, and the matrix scopes built on them: they allow exactly
/// what they should and deny everything else. These are ready for the records later scopes add.
/// </summary>
public class ResourceHandlerTests(ResourceHandlerFixture fixture) : IClassFixture<ResourceHandlerFixture>
{
    private static readonly Guid _me = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid _colleague = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid _myAgency = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid _otherAgency = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private sealed record Report(Guid OwnerUserId, Guid AgencyId) : IOwnedResource, IAgencyScopedResource;

    private sealed record JustOwned(Guid OwnerUserId) : IOwnedResource;

    private sealed record JustAgencys(Guid AgencyId) : IAgencyScopedResource;

    // Three records, as seen by someone in _myAgency who is _me.
    private static readonly Report _mine = new(_me, _myAgency);
    private static readonly Report _agencyMates = new(_colleague, _myAgency);
    private static readonly Report _foreign = new(_colleague, _otherAgency);

    private static ClaimsPrincipal User(Guid? id, Guid? agency, params Role[] roles)
    {
        var claims = new List<Claim>();
        if (id is { } userId)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        }

        if (agency is { } agencyId)
        {
            claims.Add(new Claim("cr:agency", agencyId.ToString()));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role.ToString())));
        claims.Add(new Claim("amr", "mfa"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private async Task<bool> AllowedAsync(ClaimsPrincipal user, object? resource, IAuthorizationRequirement requirement)
    {
        using var scope = fixture.Factory.Services.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, resource, requirement)).Succeeded;
    }

    // --- Own record -------------------------------------------------------------------------------

    [Fact]
    public async Task Own_record_is_allowed_for_the_owner()
    {
        Assert.True(await AllowedAsync(User(_me, _myAgency), new JustOwned(_me), new OwnRecordRequirement()));
        Assert.True(await AllowedAsync(User(_me, null), _mine, new OwnRecordRequirement()));
    }

    [Fact]
    public async Task Own_record_is_denied_for_everyone_else_and_everything_else()
    {
        var requirement = new OwnRecordRequirement();

        Assert.False(await AllowedAsync(User(_me, _myAgency), new JustOwned(_colleague), requirement)); // someone else's
        Assert.False(await AllowedAsync(User(_me, _myAgency), _agencyMates, requirement)); // same agency is not "own"
        Assert.False(await AllowedAsync(User(_me, _myAgency), new JustAgencys(_myAgency), requirement)); // owned by nobody
        Assert.False(await AllowedAsync(User(_me, _myAgency), new object(), requirement));
        Assert.False(await AllowedAsync(User(null, _myAgency), new JustOwned(_me), requirement)); // not identifiable
        Assert.False(await AllowedAsync(new ClaimsPrincipal(new ClaimsIdentity()), new JustOwned(_me), requirement)); // not signed in
    }

    [Fact]
    public async Task Own_record_cannot_be_satisfied_by_a_role()
    {
        // Even an admin does not own other people's records.
        Assert.False(await AllowedAsync(User(_me, null, Role.Admin), new JustOwned(_colleague), new OwnRecordRequirement()));
    }

    // --- Same agency ------------------------------------------------------------------------------

    [Fact]
    public async Task Same_agency_is_allowed_inside_the_users_agency()
    {
        Assert.True(await AllowedAsync(User(_me, _myAgency), new JustAgencys(_myAgency), new SameAgencyRequirement()));
        Assert.True(await AllowedAsync(User(_me, _myAgency), _agencyMates, new SameAgencyRequirement()));
        Assert.True(await AllowedAsync(User(_me, _myAgency), _mine, new SameAgencyRequirement()));
    }

    [Fact]
    public async Task Same_agency_is_denied_everywhere_else()
    {
        var requirement = new SameAgencyRequirement();

        Assert.False(await AllowedAsync(User(_me, _myAgency), new JustAgencys(_otherAgency), requirement)); // another agency
        Assert.False(await AllowedAsync(User(_me, _myAgency), _foreign, requirement));
        Assert.False(await AllowedAsync(User(_me, null), new JustAgencys(_myAgency), requirement)); // the user has no agency
        Assert.False(await AllowedAsync(User(_me, _myAgency), new JustOwned(_me), requirement)); // the record has no agency
        Assert.False(await AllowedAsync(User(_me, _myAgency), new object(), requirement));
        Assert.False(await AllowedAsync(new ClaimsPrincipal(new ClaimsIdentity()), new JustAgencys(_myAgency), requirement));
    }

    [Fact]
    public async Task A_user_with_no_agency_matches_no_record_even_one_with_an_empty_agency_id()
    {
        Assert.False(await AllowedAsync(User(_me, null, Role.Admin), new JustAgencys(Guid.Empty), new SameAgencyRequirement()));
    }

    // --- The matrix scopes, applied to records ----------------------------------------------------

    private static readonly (string Role, Capability Capability, bool Mine, bool AgencyMates, bool Foreign)[] _scopeTable =
    [
        // Reading FIC reports: an agent their own only, a compliance officer their agency's, nobody else.
        ("RegisteredAgent", Capability.ReadFicReports, true, false, false),
        ("AgencyComplianceOfficer", Capability.ReadFicReports, true, true, false),
        ("CaseReviewer", Capability.ReadFicReports, false, false, false),
        ("Admin", Capability.ReadFicReports, false, false, false),

        // Compliance status, FIC totals and the audit log: officers see their own agency, admins see all.
        ("RegisteredAgent", Capability.ViewComplianceStatus, false, false, false),
        ("AgencyComplianceOfficer", Capability.ViewComplianceStatus, true, true, false),
        ("CaseReviewer", Capability.ViewComplianceStatus, false, false, false),
        ("Admin", Capability.ViewComplianceStatus, true, true, true),

        ("RegisteredAgent", Capability.ViewFicTotals, false, false, false),
        ("AgencyComplianceOfficer", Capability.ViewFicTotals, true, true, false),
        ("CaseReviewer", Capability.ViewFicTotals, false, false, false),
        ("Admin", Capability.ViewFicTotals, true, true, true),

        ("RegisteredAgent", Capability.ReadAuditLog, false, false, false),
        ("AgencyComplianceOfficer", Capability.ReadAuditLog, true, true, false),
        ("CaseReviewer", Capability.ReadAuditLog, false, false, false),
        ("Admin", Capability.ReadAuditLog, true, true, true),
    ];

    public static TheoryData<string, Capability> ScopedCells()
    {
        var data = new TheoryData<string, Capability>();
        foreach (var cell in _scopeTable)
        {
            data.Add(cell.Role, cell.Capability);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScopedCells))]
    public async Task A_role_reaches_exactly_the_records_the_matrix_gives_it(string roleName, Capability capability)
    {
        var cell = _scopeTable.Single(entry => entry.Role == roleName && entry.Capability == capability);
        var role = Enum.Parse<Role>(roleName);

        // Staff have no agency; agents do. That is how the real sessions look.
        var user = User(_me, role.IsStaffRole() ? null : _myAgency, role);
        var requirement = new CapabilityScopeRequirement(capability);

        Assert.Equal(cell.Mine, await AllowedAsync(user, _mine, requirement));
        Assert.Equal(cell.AgencyMates, await AllowedAsync(user, _agencyMates, requirement));
        Assert.Equal(cell.Foreign, await AllowedAsync(user, _foreign, requirement));
    }

    [Fact]
    public async Task The_scope_table_covers_four_roles_for_each_of_the_four_scoped_actions()
    {
        Assert.Equal(16, _scopeTable.Length);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(Role.RegisteredAgent)]
    [InlineData(Role.AgencyComplianceOfficer)]
    [InlineData(Role.CaseReviewer)]
    [InlineData(Role.Admin)]
    public async Task An_action_that_involves_no_record_reaches_any_resource_for_every_role_that_holds_it(Role role)
    {
        var user = User(_me, role.IsStaffRole() ? null : _myAgency, role);

        // RunPreDealCheck is unscoped and held by all four roles.
        Assert.True(await AllowedAsync(user, _foreign, new CapabilityScopeRequirement(Capability.RunPreDealCheck)));
    }

    [Fact]
    public async Task A_role_that_does_not_hold_the_action_is_denied_whatever_the_record()
    {
        var agent = User(_me, _myAgency, Role.RegisteredAgent);

        Assert.False(await AllowedAsync(agent, _mine, new CapabilityScopeRequirement(Capability.ApproveAgents)));
        Assert.False(await AllowedAsync(agent, _mine, new CapabilityScopeRequirement(Capability.DecideConductCases)));
    }

    [Fact]
    public async Task A_user_with_no_roles_reaches_nothing()
    {
        var nobody = User(_me, _myAgency);

        foreach (var capability in PermissionMatrix.Capabilities)
        {
            Assert.False(await AllowedAsync(nobody, _mine, new CapabilityScopeRequirement(capability)), capability.ToString());
        }
    }

    [Fact]
    public async Task A_role_name_that_is_not_one_of_the_four_is_ignored()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, _me.ToString()), new Claim(ClaimTypes.Role, "SuperUser"), new Claim(ClaimTypes.Role, "admin ")],
            "test"));

        foreach (var capability in PermissionMatrix.Capabilities)
        {
            Assert.False(await AllowedAsync(user, _mine, new CapabilityScopeRequirement(capability)), capability.ToString());
        }
    }
}
