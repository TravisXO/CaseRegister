using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>Reading the audit log, checking it, and auditing views of sensitive records.</summary>
[Collection(PostgresTestGroup.Name)]
public class AuditEndpointTests(PostgresFixture postgres)
{
    private sealed record World(
        ApiFactory Factory,
        Agency AgencyA,
        Agency AgencyB,
        ApplicationUser Admin,
        ApplicationUser AdminTwo,
        ApplicationUser OfficerA,
        ApplicationUser OfficerB,
        ApplicationUser AgentA,
        ApplicationUser AgentB) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(Factory, user)).StatusCode);

            return client;
        }

        public Task<int> Count(string action) => Factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action));
    }

    /// <summary>Two agencies, each with a compliance officer and an agent, two admins, and some entries for each side.</summary>
    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agencyA = await factory.CreateAgencyAsync("Agency A");
        var agencyB = await factory.CreateAgencyAsync("Agency B");
        var admins = await factory.CreateAdminsAsync(2);
        var officerA = await factory.CreateMemberAsync("officer.a@example.test", agencyA, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]);
        var officerB = await factory.CreateMemberAsync("officer.b@example.test", agencyB, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]);
        var agentA = await factory.CreateMemberAsync("agent.a@example.test", agencyA, [Role.RegisteredAgent]);
        var agentB = await factory.CreateMemberAsync("agent.b@example.test", agencyB, [Role.RegisteredAgent]);

        // Some entries about each side, with addresses, so there is something to hide and something to scope.
        foreach (var (user, ip) in new[] { (agentA, "203.0.113.10"), (agentB, "203.0.113.20") })
        {
            for (var index = 0; index < 3; index++)
            {
                await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, user.Id, Subject: user.Id.ToString(), IpAddress: ip, TargetType: "User", Detail: $"viewed {index}"));
            }
        }

        await factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, null, Subject: "no-agency", TargetType: "Probe", IpAddress: "203.0.113.30"));

        return new World(factory, agencyA, agencyB, admins[0], admins[1], officerA, officerB, agentA, agentB);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static List<JsonElement> EntriesIn(JsonElement body) => [.. body.GetProperty("entries").EnumerateArray()];

    // --- Who can read it, and how much -----------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_sees_every_entry_with_the_addresses()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        var expected = (await world.Factory.AuditEntriesAsync()).Count;

        var response = await admin.GetAsync("/api/audit/entries?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("total").GetInt64() >= expected);
        var entries = EntriesIn(body);
        Assert.Contains(entries, entry => entry.GetProperty("agencyId").ValueKind == JsonValueKind.Null); // the system's own entries
        Assert.Contains(entries, entry => entry.GetProperty("agencyId").GetString() == world.AgencyA.Id.ToString());
        Assert.Contains(entries, entry => entry.GetProperty("agencyId").GetString() == world.AgencyB.Id.ToString());
        Assert.Contains(entries, entry => entry.GetProperty("ipAddress").GetString() == "203.0.113.10");
        Assert.Contains(entries, entry => entry.GetProperty("ipAddress").GetString() == "203.0.113.20");
    }

    [Fact]
    public async Task A_compliance_officer_sees_only_their_own_agencys_entries_and_no_addresses()
    {
        await using var world = await WorldAsync();
        var officer = await world.SignInAsync(world.OfficerA);
        var inDatabase = await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.AgencyId == world.AgencyA.Id));
        Assert.True(await world.Factory.WithDbAsync(db => db.AuditLog.AnyAsync(entry => entry.AgencyId == world.AgencyA.Id && entry.IpAddress != null))); // there are addresses to hide

        var response = await officer.GetAsync("/api/audit/entries?pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var entries = EntriesIn(body);
        Assert.Equal(inDatabase, body.GetProperty("total").GetInt64());
        Assert.NotEmpty(entries);
        Assert.All(entries, entry =>
        {
            Assert.Equal(world.AgencyA.Id.ToString(), entry.GetProperty("agencyId").GetString());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("ipAddress").ValueKind);
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("203.0.113", text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.AgentB.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.OfficerB.Id.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_compliance_officer_cannot_widen_their_view_with_filters()
    {
        await using var world = await WorldAsync();
        var officer = await world.SignInAsync(world.OfficerA);

        // Filtering for the other agency's agent, or its kind of entry, still shows nothing of theirs.
        var byActor = await JsonAsync(await officer.GetAsync($"/api/audit/entries?actor={world.AgentB.Id}"));
        var byAction = await JsonAsync(await officer.GetAsync("/api/audit/entries?action=RecordViewed&pageSize=100"));

        Assert.Equal(0, byActor.GetProperty("total").GetInt64());
        Assert.Empty(EntriesIn(byActor));
        Assert.NotEmpty(EntriesIn(byAction));
        Assert.All(EntriesIn(byAction), entry => Assert.Equal(world.AgencyA.Id.ToString(), entry.GetProperty("agencyId").GetString()));
    }

    [Fact]
    public async Task The_other_agencys_officer_sees_the_other_side_and_not_this_one()
    {
        await using var world = await WorldAsync();
        var officerB = await world.SignInAsync(world.OfficerB);

        var text = await (await officerB.GetAsync("/api/audit/entries?pageSize=100")).Content.ReadAsStringAsync();

        Assert.Contains(world.AgentB.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.AgentA.Id.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(world.OfficerA.Id.ToString(), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nobody_else_can_read_the_log_at_all()
    {
        await using var world = await WorldAsync();
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var unfinishedAdmin = await world.Factory.CreateUserAsync("half@example.test", staff: true, twoFactor: false);
        await world.Factory.AssignRolesAsync(unfinishedAdmin, Role.Admin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().GetAsync("/api/audit/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await world.SignInAsync(world.AgentA)).GetAsync("/api/audit/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await world.SignInAsync(reviewer)).GetAsync("/api/audit/entries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await TwoFactorFlows.RestrictedSessionAsync(world.Factory, unfinishedAdmin)).GetAsync("/api/audit/entries")).StatusCode);
    }

    [Fact]
    public async Task An_officer_of_a_suspended_agency_loses_access_at_once()
    {
        await using var world = await WorldAsync();
        var officer = await world.SignInAsync(world.OfficerA);
        Assert.Equal(HttpStatusCode.OK, (await officer.GetAsync("/api/audit/entries")).StatusCode);

        await world.Factory.SuspendAgencyAsync(world.Admin, world.AgencyA.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await officer.GetAsync("/api/audit/entries")).StatusCode);
    }

    // --- Paging and filtering --------------------------------------------------------------------------------------

    [Fact]
    public async Task Pages_are_newest_first_with_a_total_and_a_capped_size()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        var total = (await world.Factory.AuditEntriesAsync()).Count;

        var first = await JsonAsync(await admin.GetAsync("/api/audit/entries?pageSize=5"));
        var second = await JsonAsync(await admin.GetAsync("/api/audit/entries?pageSize=5&page=2"));

        var firstSequences = EntriesIn(first).Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        var secondSequences = EntriesIn(second).Select(entry => entry.GetProperty("sequence").GetInt64()).ToList();
        Assert.Equal(5, firstSequences.Count);
        Assert.Equal(firstSequences.OrderByDescending(sequence => sequence), firstSequences);
        Assert.All(secondSequences, sequence => Assert.True(sequence <= firstSequences[^1])); // the next page carries on (the read itself added one entry)
        Assert.Equal(5, first.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.True(first.GetProperty("total").GetInt64() >= total);

        Assert.Equal(100, (await JsonAsync(await admin.GetAsync("/api/audit/entries?pageSize=100000"))).GetProperty("pageSize").GetInt32());
        Assert.Equal(1, (await JsonAsync(await admin.GetAsync("/api/audit/entries?pageSize=0"))).GetProperty("pageSize").GetInt32());
        Assert.Equal(1, (await JsonAsync(await admin.GetAsync("/api/audit/entries?page=-4"))).GetProperty("page").GetInt32());
        Assert.Empty(EntriesIn(await JsonAsync(await admin.GetAsync("/api/audit/entries?page=100000"))));
    }

    [Fact]
    public async Task Entries_can_be_filtered_by_date_range_actor_and_action()
    {
        await using var world = await WorldAsync();
        var base1 = new DateTimeOffset(DateTime.UtcNow.Year + 1, 6, 1, 10, 0, 0, TimeSpan.Zero);
        world.Factory.Clock.SetUtcNow(base1);
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.SessionRevoked, world.AgentA.Id, Subject: Guid.NewGuid().ToString(), TargetType: "Session", Detail: "early"));
        world.Factory.Clock.SetUtcNow(base1.AddDays(2));
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.SessionRevoked, world.AgentB.Id, Subject: Guid.NewGuid().ToString(), TargetType: "Session", Detail: "middle"));
        world.Factory.Clock.SetUtcNow(base1.AddDays(4));
        await world.Factory.WriteAuditAsync(new AuditEntry(AuditAction.RecordViewed, world.AgentA.Id, Subject: "x", TargetType: "Probe", Detail: "late"));
        var admin = await world.SignInAsync(world.Admin); // after the clock has moved, so the session is fresh
        static string Q(DateTimeOffset when) => Uri.EscapeDataString(when.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var details = async (string query) => EntriesIn(await JsonAsync(await admin.GetAsync($"/api/audit/entries?{query}"))).Select(entry => entry.GetProperty("detail").GetString()).Where(detail => detail is "early" or "middle" or "late").ToList();

        Assert.Equal(["late", "middle"], await details($"from={Q(base1.AddDays(1))}"));
        Assert.Equal(["early"], await details($"from={Q(base1.AddHours(-1))}&to={Q(base1.AddDays(1))}"));
        Assert.Equal(["middle"], await details($"from={Q(base1.AddDays(1))}&to={Q(base1.AddDays(3))}"));
        Assert.Equal(["late", "early"], await details($"actor={world.AgentA.Id}&from={Q(base1.AddHours(-1))}"));
        Assert.Equal(["middle", "early"], await details($"action=SessionRevoked"));
        Assert.Equal(["early"], await details($"action=SessionRevoked&actor={world.AgentA.Id}"));
        Assert.Empty(await details($"action=SessionRevoked&actor={world.AgentA.Id}&from={Q(base1.AddDays(1))}"));
        Assert.Empty(await details("action=NoSuchAction"));
    }

    [Fact]
    public async Task Each_entry_carries_its_sequence_actor_role_target_trace_id_and_before_and_after()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        await admin.SendAsync(HttpMethod.Post, $"/api/admin/users/{world.AgentA.Id}/suspend", new { reason = Proposals.Reason }, forwardedFor: "203.0.113.55");

        var body = await JsonAsync(await admin.GetAsync("/api/audit/entries?action=UserSuspended"));

        var entry = Assert.Single(EntriesIn(body));
        Assert.True(entry.GetProperty("sequence").GetInt64() > 0);
        Assert.Equal("user", entry.GetProperty("actorType").GetString());
        Assert.Equal(world.Admin.Id, entry.GetProperty("actorUserId").GetGuid());
        Assert.Equal("Admin", entry.GetProperty("actorRole").GetString());
        Assert.Equal("User", entry.GetProperty("targetType").GetString());
        Assert.Equal(world.AgentA.Id.ToString(), entry.GetProperty("targetId").GetString());
        Assert.Equal("203.0.113.55", entry.GetProperty("ipAddress").GetString());
        Assert.False(string.IsNullOrEmpty(entry.GetProperty("correlationId").GetString()));
        Assert.Contains("Approved", entry.GetProperty("before").GetProperty("state").GetString(), StringComparison.Ordinal);
        Assert.Contains("Suspended", entry.GetProperty("after").GetProperty("state").GetString(), StringComparison.Ordinal);
    }

    // --- Reading is audited ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_query_writes_exactly_one_entry_saying_who_asked_what_and_how_much_they_got()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        var before = await world.Count("AuditLogRead");

        await admin.SendAsync(HttpMethod.Get, "/api/audit/entries?action=RecordViewed&pageSize=3", forwardedFor: "203.0.113.66");
        Assert.Equal(before + 1, await world.Count("AuditLogRead"));
        await admin.GetAsync("/api/audit/entries");
        await admin.GetAsync("/api/audit/entries?page=2"); // paging is the same view: one entry for each person and filter in ten minutes
        Assert.Equal(before + 2, await world.Count("AuditLogRead"));

        var entry = (await world.Factory.AuditEntriesAsync()).First(record => record.Action == "AuditLogRead" && record.Detail!.Contains("action RecordViewed", StringComparison.Ordinal));
        Assert.Equal(world.Admin.Id, entry.ActorUserId);
        Assert.Equal("Admin", entry.ActorRole);
        Assert.Equal("AuditLog", entry.TargetType);
        Assert.Equal("203.0.113.66", entry.IpAddress);
        Assert.Contains("scope all", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("page 1 size 3", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("3 of ", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_compliance_officers_reads_are_recorded_in_their_agency_and_visible_to_it()
    {
        await using var world = await WorldAsync();
        var officer = await world.SignInAsync(world.OfficerA);

        await officer.GetAsync("/api/audit/entries");
        var again = await JsonAsync(await officer.GetAsync("/api/audit/entries?action=AuditLogRead"));

        var recorded = Assert.Single(await world.Factory.AuditEntriesAsync(), record => record.Action == "AuditLogRead" && record.Detail!.Contains("scope own agency", StringComparison.Ordinal) && record.Detail.Contains("action ;", StringComparison.Ordinal) || record.Action == "AuditLogRead" && record.ActorUserId == world.OfficerA.Id && record.Detail!.Contains("page 1 size 25; ", StringComparison.Ordinal) && record.Detail.Contains("action ;", StringComparison.Ordinal));
        Assert.Equal(world.AgencyA.Id, recorded.AgencyId);
        Assert.Equal("AgencyComplianceOfficer,RegisteredAgent", recorded.ActorRole);
        Assert.Contains(EntriesIn(again), entry => entry.GetProperty("actorUserId").GetGuid() == world.OfficerA.Id); // they can see that they looked
        var otherOfficer = await world.SignInAsync(world.OfficerB);
        var theirs = await (await otherOfficer.GetAsync("/api/audit/entries?action=AuditLogRead")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(world.OfficerA.Id.ToString(), theirs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_read_writes_nothing_because_nothing_was_read()
    {
        await using var world = await WorldAsync();
        var before = await world.Count("AuditLogRead");

        await (await world.SignInAsync(world.AgentA)).GetAsync("/api/audit/entries");
        await world.Factory.CreateApiClient().GetAsync("/api/audit/entries");

        Assert.Equal(before, await world.Count("AuditLogRead"));
    }

    [Fact]
    public async Task If_the_read_cannot_be_recorded_nothing_is_returned()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        await world.Factory.BreakAuditWritesAsync();

        var status = await UserTransactions.StatusOfAsync(() => admin.GetAsync("/api/audit/entries"));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }

    // --- Verifying over HTTP --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_admin_verifies_the_chain_and_the_check_is_itself_audited()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);

        var response = await admin.SendAsync(HttpMethod.Post, "/api/audit/verify", new { }, forwardedFor: "203.0.113.88");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.True(body.GetProperty("intact").GetBoolean());
        Assert.True(body.GetProperty("entriesChecked").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("firstBreak").ValueKind);
        var logged = Assert.Single(await world.Factory.AuditEntriesAsync(), record => record.Action == "AuditVerified");
        Assert.Equal(world.Admin.Id, logged.ActorUserId);
        Assert.Equal("Admin", logged.ActorRole);
        Assert.Equal("203.0.113.88", logged.IpAddress);
        Assert.Contains("intact", logged.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_chain_is_reported_with_its_first_broken_link_and_the_check_is_still_recorded()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        await world.Factory.TamperAsync("UPDATE audit_log SET detail = 'tampered' WHERE sequence = 4");

        var body = await JsonAsync(await admin.SendAsync(HttpMethod.Post, "/api/audit/verify", new { }));

        Assert.False(body.GetProperty("intact").GetBoolean());
        var broken = body.GetProperty("firstBreak");
        Assert.Equal("BadHash", broken.GetProperty("kind").GetString());
        Assert.Equal(4, broken.GetProperty("sequence").GetInt64());
        Assert.False(string.IsNullOrEmpty(broken.GetProperty("message").GetString()));
        var logged = Assert.Single(await world.Factory.AuditEntriesAsync(), record => record.Action == "AuditVerified");
        Assert.Contains("BROKEN", logged.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verification_can_start_from_the_last_good_anchor()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);
        using (var scope = world.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuditAnchoring>().AnchorNowAsync(null);
        }

        var full = await JsonAsync(await admin.SendAsync(HttpMethod.Post, "/api/audit/verify", new { fromLastGoodAnchor = false }));
        var fromAnchor = await JsonAsync(await admin.SendAsync(HttpMethod.Post, "/api/audit/verify", new { fromLastGoodAnchor = true }));

        Assert.Equal(JsonValueKind.Null, full.GetProperty("fromAnchorSequence").ValueKind);
        Assert.True(fromAnchor.GetProperty("fromAnchorSequence").GetInt64() > 0);
        Assert.True(fromAnchor.GetProperty("entriesChecked").GetInt64() < full.GetProperty("entriesChecked").GetInt64());
        Assert.True(fromAnchor.GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task Only_admins_can_ask_for_a_verification()
    {
        await using var world = await WorldAsync();
        var before = await world.Count("AuditVerified");

        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().SendAsync(HttpMethod.Post, "/api/audit/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await world.SignInAsync(world.OfficerA)).SendAsync(HttpMethod.Post, "/api/audit/verify", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await world.SignInAsync(world.AgentA)).SendAsync(HttpMethod.Post, "/api/audit/verify", new { })).StatusCode);

        Assert.Equal(before, await world.Count("AuditVerified"));
    }

    [Fact]
    public async Task Verifying_needs_the_anti_forgery_token()
    {
        await using var world = await WorldAsync();
        var admin = await world.SignInAsync(world.Admin);

        var response = await admin.SendAsync(HttpMethod.Post, "/api/audit/verify", new { }, withCsrfToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Auditing views of sensitive records ------------------------------------------------------------------------------

    [Fact]
    public async Task A_marked_action_writes_one_entry_for_each_successful_view()
    {
        await using var world = await WorldAsync();
        var agent = await world.SignInAsync(world.AgentA);

        var first = await agent.SendAsync(HttpMethod.Get, "/probe/audited/report-7", forwardedFor: "203.0.113.99");
        await agent.GetAsync("/probe/audited/report-8");
        await agent.GetAsync("/probe/audited/report-7");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var views = (await world.Factory.AuditEntriesAsync()).Where(record => record.Action == "RecordViewed" && record.TargetType == "Probe" && record.Subject!.StartsWith("report-", StringComparison.Ordinal)).ToList();
        Assert.Equal(["report-7", "report-8", "report-7"], views.Select(record => record.Subject));
        Assert.All(views, record =>
        {
            Assert.Equal(world.AgentA.Id, record.ActorUserId);
            Assert.Equal("RegisteredAgent", record.ActorRole);
            Assert.False(string.IsNullOrEmpty(record.CorrelationId));
            Assert.Equal("viewed with GET probe/audited/{id}", record.Detail);
        });
        Assert.Equal("203.0.113.99", views[0].IpAddress);
    }

    [Fact]
    public async Task A_view_that_showed_nothing_is_not_recorded()
    {
        await using var world = await WorldAsync();
        var agent = await world.SignInAsync(world.AgentA);
        var before = await world.Count("RecordViewed");

        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync("/probe/audited-missing/nothing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().GetAsync("/probe/audited/report-9")).StatusCode);

        Assert.Equal(before, await world.Count("RecordViewed"));
    }

    [Fact]
    public async Task A_record_is_not_shown_if_its_view_cannot_be_recorded()
    {
        await using var world = await WorldAsync();
        var agent = await world.SignInAsync(world.AgentA);
        await world.Factory.BreakAuditWritesAsync();

        var status = await UserTransactions.StatusOfAsync(() => agent.GetAsync("/probe/audited/report-7"));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
    }
}
