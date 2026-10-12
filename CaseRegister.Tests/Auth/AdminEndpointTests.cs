using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

/// <summary>The admin endpoints, over real HTTP with real sessions, cookies, anti-forgery tokens and two-factor codes.</summary>
[Collection(PostgresTestGroup.Name)]
public class AdminEndpointTests(PostgresFixture postgres)
{
    private sealed record World(
        ApiFactory Factory,
        ApplicationUser A,
        ApplicationUser B,
        ApplicationUser C,
        Agency Agency,
        ApplicationUser Member,
        ApplicationUser Staff) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(Factory, user)).StatusCode);

            return client;
        }

        public static object ProposeBody(string type, Guid? userId = null, Guid? agencyId = null, string? role = null, Guid? certificateId = null, string? reason = null) =>
            new { type, targetUserId = userId, targetAgencyId = agencyId, role, certificateId, reason = reason ?? Proposals.Reason };

        public static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>();

        public static async Task<JsonElement> ProposeAsync(ApiClient client, object body)
        {
            var response = await client.SendAsync(HttpMethod.Post, "/api/admin/pending-actions", body);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return await Json(response);
        }

        public static object ConfirmBody(JsonElement proposal, string code) => new { payloadHash = proposal.GetProperty("payloadHash").GetString(), twoFactorCode = code };
    }

    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync("Copperbelt Homes");
        var admins = await factory.CreateAdminsAsync(3);
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        return new World(factory, admins[0], admins[1], admins[2], agency, member, staff);
    }

    // --- Who can reach what ---------------------------------------------------------------------------

    private static IEnumerable<(HttpMethod Method, string Path)> AllEndpoints(Guid id) =>
    [
        (HttpMethod.Get, "/api/admin/pending-actions"),
        (HttpMethod.Get, "/api/admin/applications"),
        (HttpMethod.Get, $"/api/admin/pending-actions/{id}"),
        (HttpMethod.Post, "/api/admin/pending-actions"),
        (HttpMethod.Post, $"/api/admin/pending-actions/{id}/confirm"),
        (HttpMethod.Post, $"/api/admin/pending-actions/{id}/reject"),
        (HttpMethod.Post, $"/api/admin/pending-actions/{id}/withdraw"),
        (HttpMethod.Post, $"/api/admin/users/{id}/suspend"),
        (HttpMethod.Post, $"/api/admin/users/{id}/end-sessions"),
        (HttpMethod.Post, $"/api/admin/agencies/{id}/suspend"),
    ];

    [Fact]
    public async Task Nobody_but_a_fully_signed_in_admin_reaches_any_admin_endpoint()
    {
        await using var world = await WorldAsync();
        var officer = await world.Factory.CreateMemberAsync("officer@example.test", world.Agency, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]);
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var adminWithoutTwoFactor = await world.Factory.CreateUserAsync("bare@example.test", staff: true, twoFactor: false);
        await world.Factory.AssignRolesAsync(adminWithoutTwoFactor, Role.Admin);

        var callers = new Dictionary<string, ApiClient>
        {
            ["anonymous"] = world.Factory.CreateApiClient(),
            ["an agent"] = await world.SignInAsync(world.Member),
            ["a compliance officer"] = await world.SignInAsync(officer),
            ["a case reviewer"] = await world.SignInAsync(reviewer),
            ["an admin who has not done two-factor"] = await TwoFactorFlows.RestrictedSessionAsync(world.Factory, adminWithoutTwoFactor),
        };
        var half = await TwoFactorFlows.AtTheCodeStepAsync(world.Factory, world.A);
        callers["an admin half way through signing in"] = half;

        foreach (var (name, client) in callers)
        {
            foreach (var (method, path) in AllEndpoints(Guid.NewGuid()))
            {
                var response = await client.SendAsync(method, path, method == HttpMethod.Post ? new { reason = Proposals.Reason } : null);
                var expected = name is "anonymous" or "an admin half way through signing in" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;

                Assert.True(response.StatusCode == expected, $"{name}: {method} {path} gave {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task A_signed_in_admin_gets_past_the_door_to_every_admin_endpoint()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.A);

        foreach (var (method, path) in AllEndpoints(Guid.NewGuid()))
        {
            var response = await client.SendAsync(method, path, method == HttpMethod.Post ? new { reason = Proposals.Reason } : null);

            Assert.True(
                response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden),
                $"{method} {path} gave {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Every_unsafe_admin_request_needs_the_anti_forgery_token()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.A);

        foreach (var (method, path) in AllEndpoints(Guid.NewGuid()).Where(endpoint => endpoint.Method == HttpMethod.Post))
        {
            var response = await client.SendAsync(method, path, new { reason = Proposals.Reason }, withCsrfToken: false);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{path} gave {(int)response.StatusCode}");
        }
    }

    // --- Propose, list, confirm --------------------------------------------------------------------------

    [Fact]
    public async Task One_admin_proposes_another_confirms_with_the_hash_and_a_fresh_code_and_it_is_applied()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);

        var proposal = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));

        Assert.Equal("AssignRole", proposal.GetProperty("type").GetString());
        Assert.Equal("Pending", proposal.GetProperty("status").GetString());
        Assert.Equal("CaseReviewer", proposal.GetProperty("payload").GetProperty("role").GetString());
        Assert.Equal(world.Staff.Id, proposal.GetProperty("target").GetProperty("userId").GetGuid());
        Assert.Equal(world.A.Id, proposal.GetProperty("proposedBy").GetProperty("userId").GetGuid());
        Assert.Equal(proposal.GetProperty("proposedAt").GetDateTimeOffset().AddHours(72), proposal.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Empty(await world.Factory.RolesOfAsync(world.Staff));

        // The other admin sees it in the list, with the hash they will have to quote.
        var listed = await World.Json(await checker.GetAsync("/api/admin/pending-actions"));
        var item = Assert.Single(listed.EnumerateArray());
        Assert.Equal(proposal.GetProperty("id").GetGuid(), item.GetProperty("id").GetGuid());
        Assert.Equal(proposal.GetProperty("payloadHash").GetString(), item.GetProperty("payloadHash").GetString());

        var confirm = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm", World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.B)));

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmed = await World.Json(confirm);
        Assert.Equal("Confirmed", confirmed.GetProperty("status").GetString());
        Assert.Equal(world.B.Id, confirmed.GetProperty("decidedByUserId").GetGuid());
        Assert.Equal([Role.CaseReviewer], await world.Factory.RolesOfAsync(world.Staff));
        Assert.Empty((await World.Json(await checker.GetAsync("/api/admin/pending-actions"))).EnumerateArray()); // nothing is waiting any more
    }

    [Fact]
    public async Task A_session_alone_is_not_enough_to_confirm_a_code_is_needed_and_the_one_used_to_sign_in_is_spent()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);
        var proposal = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));
        var path = $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm";
        var hash = proposal.GetProperty("payloadHash").GetString();

        // No code at all.
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.SendAsync(HttpMethod.Post, path, new { payloadHash = hash })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.SendAsync(HttpMethod.Post, path, new { payloadHash = hash, twoFactorCode = "" })).StatusCode);

        // The code that was used to sign in a moment ago, in the same time step.
        var signInCode = world.Factory.CodeForStep(world.B, world.Factory.CurrentStep);
        Assert.Equal(HttpStatusCode.Unauthorized, (await checker.SendAsync(HttpMethod.Post, path, new { payloadHash = hash, twoFactorCode = signInCode })).StatusCode);

        // A wrong one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await checker.SendAsync(HttpMethod.Post, path, new { payloadHash = hash, twoFactorCode = "000000" })).StatusCode);

        Assert.Empty(await world.Factory.RolesOfAsync(world.Staff));
        Assert.Equal(HttpStatusCode.OK, (await checker.SendAsync(HttpMethod.Post, path, World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.B)))).StatusCode);
    }

    [Fact]
    public async Task Confirming_with_the_wrong_hash_is_a_conflict_and_changes_nothing()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);
        var proposal = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));

        var response = await checker.SendAsync(
            HttpMethod.Post,
            $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm",
            new { payloadHash = new string('a', 64), twoFactorCode = world.Factory.NextTotpCode(world.B) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(await world.Factory.RolesOfAsync(world.Staff));
    }

    [Fact]
    public async Task The_proposer_cannot_confirm_and_a_repeat_by_the_confirmer_is_the_same_answer()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);
        var third = await world.SignInAsync(world.C);
        var proposal = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));
        var path = $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm";

        Assert.Equal(HttpStatusCode.Forbidden, (await maker.SendAsync(HttpMethod.Post, path, World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.A)))).StatusCode);

        var first = await checker.SendAsync(HttpMethod.Post, path, World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.B)));
        var again = await checker.SendAsync(HttpMethod.Post, path, World.ConfirmBody(proposal, "000000")); // no code is asked for a repeat
        var late = await third.SendAsync(HttpMethod.Post, path, World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.C)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal((await first.Content.ReadAsStringAsync()), await again.Content.ReadAsStringAsync()); // byte for byte
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); // someone else: already decided
        Assert.Equal([Role.CaseReviewer], await world.Factory.RolesOfAsync(world.Staff));
    }

    [Fact]
    public async Task A_rule_that_no_longer_holds_is_a_422_with_the_reason_and_the_proposal_stays_pending()
    {
        await using var world = await WorldAsync();
        var officer = await world.Factory.CreateMemberAsync("officer@example.test", world.Agency, [Role.RegisteredAgent]);
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);
        var proposal = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: officer.Id, role: "AgencyComplianceOfficer"));
        await world.Factory.TryChangeRolesAsync(new CaseRegister.Application.Accounts.RoleChange(officer.Id, [], [Role.RegisteredAgent]));
        var id = proposal.GetProperty("id").GetGuid();

        var response = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/confirm", World.ConfirmBody(proposal, world.Factory.NextTotpCode(world.B)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await World.Json(response);
        Assert.Equal(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, problem.GetProperty("problems")[0].GetProperty("code").GetString());
        var stored = await World.Json(await checker.GetAsync($"/api/admin/pending-actions/{id}"));
        Assert.Equal("Pending", stored.GetProperty("status").GetString());
        Assert.Equal(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, stored.GetProperty("lastRefusal").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reject_and_withdraw_work_over_http_and_need_a_reason()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var checker = await world.SignInAsync(world.B);
        var one = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));
        var oneId = one.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{oneId}/reject", new { reason = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{oneId}/reject", new { reason = Proposals.Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{oneId}/withdraw", new { reason = Proposals.Reason })).StatusCode);

        var rejected = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{oneId}/reject", new { reason = "This access is not needed." });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal("Rejected", (await World.Json(rejected)).GetProperty("status").GetString());

        var two = await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));
        var withdrawn = await maker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{two.GetProperty("id").GetGuid()}/withdraw", new { reason = "Proposed by mistake, sorry." });
        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        Assert.Equal("Withdrawn", (await World.Json(withdrawn)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{oneId}/reject", new { reason = Proposals.Reason })).StatusCode); // already decided
    }

    [Fact]
    public async Task The_list_leaves_out_expired_proposals_unless_closed_ones_are_asked_for()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        await World.ProposeAsync(maker, World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "CaseReviewer"));

        Assert.Single((await World.Json(await maker.GetAsync("/api/admin/pending-actions"))).EnumerateArray());

        world.Factory.Clock.Advance(TimeSpan.FromHours(72));
        var later = await world.SignInAsync(world.A); // the old session has long since timed out

        Assert.Empty((await World.Json(await later.GetAsync("/api/admin/pending-actions"))).EnumerateArray());
        var closed = Assert.Single((await World.Json(await later.GetAsync("/api/admin/pending-actions?includeClosed=true"))).EnumerateArray());
        Assert.Equal("Expired", closed.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Proposals_that_make_no_sense_are_turned_away()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        const string path = "/api/admin/pending-actions";

        // Wrong shape or wrong values: 400.
        foreach (var body in new object[]
        {
            World.ProposeBody("AssignRole", userId: world.Staff.Id), // no role
            World.ProposeBody("AssignRole", userId: world.Staff.Id, role: "Chancellor"), // not a role
            World.ProposeBody("Frobnicate", userId: world.Staff.Id), // not an action
            World.ProposeBody("ResetTwoFactor", userId: world.Staff.Id, reason: "short"),
            World.ProposeBody("ResetTwoFactor", userId: world.Staff.Id, agencyId: world.Agency.Id),
            new { type = 1, targetUserId = world.Staff.Id, reason = Proposals.Reason }, // numbers are not accepted for names
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await maker.SendAsync(HttpMethod.Post, path, body)).StatusCode);
        }

        // Aimed at yourself: 403. At nobody: 404. Needs no approval: 400. Not possible now: 422.
        Assert.Equal(HttpStatusCode.Forbidden, (await maker.SendAsync(HttpMethod.Post, path, World.ProposeBody("ResetTwoFactor", userId: world.A.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maker.SendAsync(HttpMethod.Post, path, World.ProposeBody("ResetTwoFactor", userId: Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.SendAsync(HttpMethod.Post, path, World.ProposeBody("SuspendUser", userId: world.Member.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await maker.SendAsync(HttpMethod.Post, path, World.ProposeBody("AssignRole", userId: world.Member.Id, role: "Admin"))).StatusCode);

        // The same thing twice: 409.
        await World.ProposeAsync(maker, World.ProposeBody("ResetTwoFactor", userId: world.Staff.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await maker.SendAsync(HttpMethod.Post, path, World.ProposeBody("ResetTwoFactor", userId: world.Staff.Id))).StatusCode);
    }

    [Fact]
    public async Task Asking_about_a_proposal_that_does_not_exist_is_a_404()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.A);
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/pending-actions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/confirm", new { payloadHash = "a", twoFactorCode = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/reject", new { reason = Proposals.Reason })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/withdraw", new { reason = Proposals.Reason })).StatusCode);
    }

    // --- Taking access away at once, over HTTP ---------------------------------------------------------------

    [Fact]
    public async Task One_admin_suspends_a_member_over_http_and_the_members_session_ends_at_once()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.A);
        var member = await world.SignInAsync(world.Member);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/me")).StatusCode);

        var response = await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/suspend", new { reason = "Complaint under investigation, pending review." });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/me")).StatusCode);
        Assert.Equal(AccountStatus.Suspended, (await world.Factory.GetUserAsync(world.Member.Email!)).AccountStatus);
    }

    [Fact]
    public async Task Ending_sessions_and_suspending_an_agency_work_over_http()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.A);
        var member = await world.SignInAsync(world.Member);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/agencies/{world.Agency.Id}/suspend", new { reason = "Licence under review by the association." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/end-sessions", new { reason = "Device reported lost by the member." })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task The_immediate_endpoints_refuse_what_they_must()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.A);
        var reason = new { reason = Proposals.Reason };

        // An admin as the target: it needs a second admin, so 403 and nothing happens.
        var adminTarget = await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.B.Id}/suspend", reason);
        Assert.Equal(HttpStatusCode.Forbidden, adminTarget.StatusCode);
        Assert.Equal("Needs a second admin", (await World.Json(adminTarget)).GetProperty("title").GetString());
        Assert.Equal(AccountStatus.Approved, (await world.Factory.GetUserAsync(world.B.Email!)).AccountStatus);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.A.Id}/suspend", reason)).StatusCode); // yourself
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{Guid.NewGuid()}/suspend", reason)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/agencies/{Guid.NewGuid()}/suspend", reason)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/suspend", new { reason = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/suspend", new { })).StatusCode);

        Assert.Equal(AccountStatus.Approved, (await world.Factory.GetUserAsync(world.Member.Email!)).AccountStatus);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/suspend", reason)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.Member.Id}/suspend", reason)).StatusCode); // already suspended
    }

    [Fact]
    public async Task Nothing_the_admin_endpoints_say_gives_away_a_secret()
    {
        await using var world = await WorldAsync();
        var maker = await world.SignInAsync(world.A);
        var proposal = await World.ProposeAsync(maker, World.ProposeBody("ResetTwoFactor", userId: world.Staff.Id));
        var text = proposal.GetRawText() + await (await maker.GetAsync("/api/admin/pending-actions")).Content.ReadAsStringAsync();

        foreach (var forbidden in new[] { "encryptedKey", "securityStamp", "passwordHash", "sharedKey", "otpauth" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

    }
}
