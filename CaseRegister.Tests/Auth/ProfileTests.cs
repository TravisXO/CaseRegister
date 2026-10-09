using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Domain.Accounts;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class ProfileTests(PostgresFixture postgres)
{
    private static async Task<JsonElement> ProfileOfAsync(ApiFactory factory, Infrastructure.Identity.ApplicationUser user)
    {
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);
        var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task A_members_profile_shows_their_membership_class_and_membership_number_with_the_configured_prefix()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync("Copperbelt Homes");
        var member = await factory.CreateUserAsync("jane@example.test", fullName: "Jane Mwansa", membershipNumber: 884, agencyId: agency.Id, membershipClass: MembershipClass.Honorary);
        await factory.AssignRolesAsync(member, Role.RegisteredAgent);

        var profile = await ProfileOfAsync(factory, member);

        Assert.Equal("Jane Mwansa", profile.GetProperty("fullName").GetString());
        Assert.Equal("jane@example.test", profile.GetProperty("email").GetString());
        Assert.Equal("AGT 884", profile.GetProperty("membershipNumber").GetString()); // "<prefix> <number>"
        Assert.Equal("Honorary", profile.GetProperty("membershipClass").GetString());
        Assert.Equal("Copperbelt Homes", profile.GetProperty("agency").GetProperty("name").GetString());
        Assert.Equal(agency.Id, profile.GetProperty("agency").GetProperty("id").GetGuid());
        Assert.Equal(["RegisteredAgent"], profile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("Associate")]
    [InlineData("Student")]
    [InlineData("Honorary")]
    public async Task Every_membership_class_is_stored_and_shown(string className)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateUserAsync("jane@example.test", agencyId: agency.Id, membershipClass: Enum.Parse<MembershipClass>(className));

        Assert.Equal(className, (await ProfileOfAsync(factory, member)).GetProperty("membershipClass").GetString());
        Assert.Equal(Enum.Parse<MembershipClass>(className), (await factory.GetUserAsync("jane@example.test")).MembershipClass);
    }

    [Fact]
    public async Task The_membership_class_is_stored_as_readable_text()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane@example.test", membershipClass: MembershipClass.Student);

        var raw = await factory.WithDbAsync(db => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions
            .SqlQuery<string>(db.Database, $"SELECT membership_class AS \"Value\" FROM users")
            .SingleAsync());

        Assert.Equal("Student", raw);
    }

    [Fact]
    public async Task The_membership_class_changes_nothing_about_what_the_profile_says_of_access()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var honoraryWithout = await factory.CreateMemberAsync("honorary@example.test", agency, [Role.RegisteredAgent], currentCertificate: false, MembershipClass.Honorary);
        var fullWith = await factory.CreateMemberAsync("full@example.test", agency, [Role.RegisteredAgent], currentCertificate: true, MembershipClass.Full);

        Assert.False((await ProfileOfAsync(factory, honoraryWithout)).GetProperty("hasCurrentPractisingCertificate").GetBoolean());
        Assert.True((await ProfileOfAsync(factory, fullWith)).GetProperty("hasCurrentPractisingCertificate").GetBoolean());
    }

    [Fact]
    public async Task The_prefix_comes_from_the_one_configuration_setting()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings => settings["Membership:DisplayPrefix"] = "REG");
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateUserAsync("jane@example.test", membershipNumber: 884, agencyId: agency.Id);

        Assert.Equal("REG 884", (await ProfileOfAsync(factory, member)).GetProperty("membershipNumber").GetString());
    }

    [Fact]
    public async Task Association_staff_have_no_membership_class_number_or_agency()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admin = await factory.CreateStaffAsync("admin@example.test", Role.Admin);

        var profile = await ProfileOfAsync(factory, admin);

        Assert.Equal(JsonValueKind.Null, profile.GetProperty("membershipNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("membershipClass").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("agency").ValueKind);
        Assert.Equal(["Admin"], profile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public async Task The_profile_holds_nothing_sensitive()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateUserAsync("jane@example.test", agencyId: agency.Id);

        var profile = await ProfileOfAsync(factory, member);

        Assert.Equal(
            ["agency", "email", "fullName", "hasCurrentPractisingCertificate", "id", "membershipClass", "membershipNumber", "roles"],
            profile.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_member_sees_only_their_own_profile()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var alice = await factory.CreateUserAsync("alice@example.test", fullName: "Alice A", agencyId: agency.Id);
        await factory.CreateUserAsync("bob@example.test", fullName: "Bob B", agencyId: agency.Id);

        Assert.Equal("Alice A", (await ProfileOfAsync(factory, alice)).GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task The_profile_needs_a_fully_signed_in_session()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("jane@example.test");
        var anonymous = factory.CreateApiClient();
        var halfWay = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await halfWay.GetAsync("/api/me")).StatusCode);

        var withoutTwoFactor = await factory.CreateUserAsync("new@example.test", twoFactor: false);
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(factory, withoutTwoFactor);
        Assert.Equal(HttpStatusCode.Forbidden, (await restricted.GetAsync("/api/me")).StatusCode);
    }
}
