using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

/// <summary>The audit log and the proposals read by names, not ids; one place for the words for roles; and no email address, name or plain hash in what the log stores.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class AuditReadabilityTests(PostgresFixture postgres)
{
    private sealed record World(ApiFactory Factory, Agency AgencyA, Agency AgencyB, ApplicationUser Admin, ApplicationUser Second, ApplicationUser OfficerA, ApplicationUser AgentA, ApplicationUser AgentB) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(Factory, user)).StatusCode);

            return client;
        }
    }

    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var a = await factory.CreateAgencyAsync("Copperbelt Homes");
        var b = await factory.CreateAgencyAsync("Lusaka Lettings");
        var admin = await factory.CreateUserAsync("admin.one@example.test", staff: true, fullName: "Mwila Chanda");
        await factory.AssignRolesAsync(admin, Role.Admin);
        var second = await factory.CreateUserAsync("admin.two@example.test", staff: true, fullName: "Thandiwe Phiri");
        await factory.AssignRolesAsync(second, Role.Admin);
        var officer = await factory.CreateUserAsync("officer@example.test", agencyId: a.Id, fullName: "Chipo Mulenga");
        await factory.AssignRolesAsync(officer, Role.RegisteredAgent, Role.AgencyComplianceOfficer);
        await factory.AddCertificateAsync(officer);
        var agentA = await factory.CreateMemberAsync("agent.a@example.test", a, [Role.RegisteredAgent]);
        var agentB = await factory.CreateUserAsync("agent.b@example.test", agencyId: b.Id, fullName: "Kelvin Sakala");
        await factory.AssignRolesAsync(agentB, Role.RegisteredAgent);

        return new World(factory, a, b, admin, second, officer, agentA, agentB);
    }

    private static async Task<List<JsonElement>> EntriesAsync(ApiClient client, string query = "")
    {
        var response = await client.GetAsync("/api/audit/entries?pageSize=100" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("entries").EnumerateArray()];
    }

    private static readonly string[] _namesOfThatAgency = ["Chipo Mulenga", "Test Person"];

    private static string? Text(JsonElement entry, string property) => entry.GetProperty(property).ValueKind == JsonValueKind.Null ? null : entry.GetProperty(property).GetString();

    // --- Names instead of ids ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_is_given_the_names_of_who_did_it_and_what_it_was_done_to_and_still_the_ids()
    {
        await using var world = await WorldAsync();
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, world.OfficerA.Id, Subject: world.AgentB.Id.ToString(), TargetType: "User", Detail: "looked"));
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.AgencySuspended, world.Admin.Id, Subject: world.AgencyB.Id.ToString(), TargetType: "Agency", Detail: "done"));
        var admin = await world.SignInAsync(world.Admin);

        var entries = await EntriesAsync(admin);

        var viewed = entries.Single(entry => Text(entry, "action") == "RecordViewed");
        Assert.Equal("Chipo Mulenga", Text(viewed, "actorName"));
        Assert.Equal("Kelvin Sakala", Text(viewed, "targetName"));
        Assert.Equal(world.OfficerA.Id.ToString(), viewed.GetProperty("actorUserId").GetGuid().ToString()); // for the admin who expands the row
        Assert.Equal(world.AgentB.Id.ToString(), Text(viewed, "targetId"));
        var agency = entries.Single(entry => Text(entry, "action") == "AgencySuspended");
        Assert.Equal("Mwila Chanda", Text(agency, "actorName"));
        Assert.Equal("Lusaka Lettings", Text(agency, "targetName"));
    }

    [Fact]
    public async Task A_compliance_officer_is_given_names_only_for_their_own_agency_and_never_an_address()
    {
        await using var world = await WorldAsync();
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, world.AgentA.Id, Subject: world.AgentA.Id.ToString(), TargetType: "User", IpAddress: "203.0.113.10"));
        var officer = await world.SignInAsync(world.OfficerA);

        var entries = await EntriesAsync(officer);

        Assert.NotEmpty(entries);
        Assert.All(entries, entry => Assert.Null(Text(entry, "ipAddress"))); // never shown to a compliance officer
        var mine = entries.Where(entry => Text(entry, "actorName") is not null).ToList();
        Assert.All(mine, entry => Assert.Contains(Text(entry, "actorName"), _namesOfThatAgency));
        var text = JsonSerializer.Serialize(entries);
        Assert.DoesNotContain("Kelvin Sakala", text, StringComparison.Ordinal); // another agency's member
        Assert.DoesNotContain("Mwila Chanda", text, StringComparison.Ordinal); // an admin who acted
        Assert.DoesNotContain("Thandiwe Phiri", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Lusaka Lettings", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_about_an_email_address_carry_no_name_and_the_viewer_understands_the_new_and_the_old_form()
    {
        await using var world = await WorldAsync();
        await world.Factory.CreateApiClient().SignInAsync("stranger@example.test"); // the new form
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.SignInFailed, null, Subject: "email-sha256:aaaaaaaaaaaaaaaa", Detail: "wrong password")); // an old entry
        var admin = await world.SignInAsync(world.Admin);

        var entries = (await EntriesAsync(admin)).Where(entry => Text(entry, "action") == "SignInFailed").ToList();

        Assert.Contains(entries, entry => Text(entry, "targetId")!.StartsWith("email-hmac:p1:", StringComparison.Ordinal));
        Assert.Contains(entries, entry => Text(entry, "targetId") == "email-sha256:aaaaaaaaaaaaaaaa");
        Assert.All(entries, entry =>
        {
            Assert.Equal("Email", Text(entry, "targetType"));
            Assert.Null(Text(entry, "targetName"));
            Assert.Null(Text(entry, "actorName"));
        });
    }

    // --- Proposals ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_confirmed_proposal_names_who_proposed_and_who_confirmed_it()
    {
        await using var world = await WorldAsync();
        var staff = await world.Factory.CreateUserAsync("staff@example.test", staff: true, fullName: "Natasha Banda");
        var proposal = await world.Factory.ProposeOkAsync(world.Admin, Proposals.AssignRole(staff, Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.Second, proposal);
        var viewer = await world.SignInAsync(world.Admin);

        var response = await viewer.GetAsync($"/api/admin/pending-actions/{proposal.Id}");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Mwila Chanda", body.GetProperty("proposedBy").GetProperty("name").GetString());
        Assert.Equal("Thandiwe Phiri", body.GetProperty("decidedByName").GetString());
        Assert.Equal("Give Natasha Banda the Case reviewer role", body.GetProperty("description").GetString());
        Assert.Equal(world.Second.Id, body.GetProperty("decidedByUserId").GetGuid()); // the id stays for the expand
    }

    // --- Words for roles --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_role_has_one_plain_label_and_nothing_else_says_it_differently()
    {
        Assert.Equal("Registered agent", RoleLabels.For(Role.RegisteredAgent));
        Assert.Equal("Agency compliance officer", RoleLabels.For(Role.AgencyComplianceOfficer));
        Assert.Equal("Case reviewer", RoleLabels.For(Role.CaseReviewer));
        Assert.Equal("Admin", RoleLabels.For(Role.Admin));
        foreach (var role in Enum.GetValues<Role>())
        {
            if (role != Role.Admin)
            {
                Assert.NotEqual(role.ToString(), RoleLabels.For(role)); // a code made plain
            }

            Assert.Equal(RoleLabels.For(role), RoleLabels.For(role.ToString()));
        }

        Assert.Equal("Something else", RoleLabels.For("Something else"));
    }

    [Fact]
    public void The_sentences_the_server_writes_use_the_labels_not_the_codes()
    {
        var assign = ActionPayloads.Describe(PendingActionType.AssignRole, "{\"role\":\"AgencyComplianceOfficer\"}", "Chipo Mulenga");
        var remove = ActionPayloads.Describe(PendingActionType.RemoveRole, "{\"role\":\"RegisteredAgent\"}", "Chipo Mulenga");
        var invite = ActionPayloads.Describe(PendingActionType.InviteStaff, "{\"email\":\"a@example.test\",\"fullName\":\"Ann Phiri\",\"role\":\"CaseReviewer\"}", null);

        Assert.Equal("Give Chipo Mulenga the Agency compliance officer role", assign);
        Assert.Equal("Take the Registered agent role away from Chipo Mulenga", remove);
        Assert.Contains("as Case reviewer.", invite, StringComparison.Ordinal);
        foreach (var code in new[] { "AgencyComplianceOfficer", "RegisteredAgent", "CaseReviewer" })
        {
            Assert.DoesNotContain(code, assign + remove + invite, StringComparison.Ordinal);
        }
    }

    // --- What the log stores ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task No_audit_entry_holds_an_email_address_a_display_name_or_a_plain_hash_of_either_after_the_whole_flow()
    {
        await using var world = await WorldAsync();
        var (stranger, applicant, invitee) = ("stranger@example.test", "applicant@example.test", "invitee@example.test");

        // Failed sign-ins for someone unknown and for a real person, a sign-up, a repeated sign-up, an invite and its confirmation, a proposal about a person.
        await world.Factory.CreateApiClient().SignInAsync(stranger);
        await world.Factory.CreateApiClient().SignInAsync(world.AgentA.Email!, "wrong password here");
        await SignUps.PostAsync(world.Factory.CreateApiClient(), SignUps.Body(world.Factory, applicant, "Mulenga Tembo"));
        await SignUps.PostAsync(world.Factory.CreateApiClient(), SignUps.Body(world.Factory, world.AgentA.Email!, "Another Name"));
        var invite = await world.Factory.ProposeOkAsync(world.Admin, Proposals.Invite(invitee, "Ivy Zulu", Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.Second, invite);
        var staff = await world.Factory.CreateUserAsync("staff@example.test", staff: true, fullName: "Natasha Banda");
        await world.Factory.ProposeAndConfirmAsync(world.Admin, world.Second, Proposals.AssignRole(staff, Role.CaseReviewer));
        var admin = await world.SignInAsync(world.Admin);
        await admin.GetAsync("/api/admin/pending-actions");

        var everything = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        Assert.True(everything.Count > 10);
        var text = string.Join('\n', everything.SelectMany(entry => new[] { entry.Subject, entry.Detail, entry.BeforeJson, entry.AfterJson, entry.TargetType, entry.ActorRole, entry.CorrelationId }.OfType<string>()));

        Assert.DoesNotContain("@", text, StringComparison.Ordinal);
        foreach (var name in new[] { "Mwila Chanda", "Thandiwe Phiri", "Chipo Mulenga", "Kelvin Sakala", "Mulenga Tembo", "Another Name", "Ivy Zulu", "Natasha Banda", "Test Person" })
        {
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var email in new[] { stranger, applicant, invitee, world.AgentA.Email!, world.Admin.Email! })
        {
            Assert.DoesNotContain(Hex(SHA256.HashData(Encoding.UTF8.GetBytes(email)))[..16], text, StringComparison.OrdinalIgnoreCase);
        }

        // What is there for an address is the keyed form, with the key's id, and nothing in the new form is the old one.
        var labels = everything.Select(entry => entry.Subject).Where(AuditSubject.IsEmailLabel).ToList();
        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.StartsWith("email-hmac:p1:", label, StringComparison.Ordinal));
        Assert.DoesNotContain("email-sha256:", text, StringComparison.Ordinal);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
