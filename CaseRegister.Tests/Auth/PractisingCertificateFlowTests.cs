using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class PractisingCertificateFlowTests(PostgresFixture postgres)
{
    private sealed record World(ApiFactory Factory, Agency Agency, ApplicationUser Agent, ApplicationUser Admin) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(Factory, user)).StatusCode);

            return client;
        }

        public string Number(int sequence, int? year = null) => $"{sequence}/{year ?? Factory.Today.Year}";

        public static object Body(string number, int year) =>
            new { certificateNumber = number, validFrom = $"{year}-01-01", validTo = $"{year}-12-31" };
    }

    /// <summary>An agent with the registered-agent role and no certificate, and an admin who can verify.</summary>
    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var agent = await factory.CreateMemberAsync("agent@example.test", agency, [Role.RegisteredAgent], currentCertificate: false);
        var admin = await factory.CreateStaffAsync("admin@example.test", Role.Admin);

        return new World(factory, agency, agent, admin);
    }

    private static async Task<VerifyCertificateOutcome> VerifyAsync(ApiFactory factory, Guid certificateId, Guid byUserId)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPractisingCertificateService>().VerifyAsync(certificateId, byUserId);
    }

    private static async Task<bool> HasCurrentAsync(ApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IPractisingCertificateService>().HasCurrentAsync(userId);
    }

    private static string[] AgentCapabilities { get; } =
    [
        "RunPreDealCheck", "FileConductReport", "ViewUpheldCases", "RaiseInternalSuspicion", "ReadFicReports",
    ];

    // --- Adding a certificate -----------------------------------------------------------------------

    [Fact]
    public async Task A_member_adds_a_certificate_and_it_starts_unverified_and_does_not_count()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);
        var year = world.Factory.Today.Year;

        var response = await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), year));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var stored = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync());
        Assert.Equal(world.Agent.Id, stored.UserId);
        Assert.Equal($"9611/{year}", stored.Number.ToString());
        Assert.False(stored.IsVerified);
        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task The_member_can_list_their_certificates_newest_year_first()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);
        var year = world.Factory.Today.Year;
        await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(100, year - 1), year - 1));
        await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(200, year), year));

        var list = await (await client.GetAsync("/api/me/practising-certificates")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal([$"200/{year}", $"100/{year - 1}"], list.EnumerateArray().Select(item => item.GetProperty("number").GetString()));
        Assert.All(list.EnumerateArray(), item => Assert.False(item.GetProperty("isVerified").GetBoolean()));
    }

    [Fact]
    public async Task A_member_only_ever_sees_their_own_certificates()
    {
        await using var world = await WorldAsync();
        var other = await world.Factory.CreateMemberAsync("other@example.test", world.Agency, [Role.RegisteredAgent], currentCertificate: true);
        var client = await world.SignInAsync(world.Agent);

        var list = await (await client.GetAsync("/api/me/practising-certificates")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, list.GetArrayLength());
        Assert.NotNull(other);
    }

    [Theory]
    [InlineData("")]
    [InlineData("9611")]
    [InlineData("9611/26")]
    [InlineData("1234567/2026")]
    [InlineData("0/2026")]
    [InlineData("9611-2026")]
    [InlineData("abc/2026")]
    public async Task A_certificate_number_that_is_not_digits_slash_year_is_refused(string number)
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);

        var response = await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(number, 2026));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync()));
    }

    [Fact]
    public async Task The_year_in_the_number_must_match_the_validity_dates()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);

        var response = await client.SendAsync(
            HttpMethod.Post,
            "/api/me/practising-certificates",
            new { certificateNumber = "9611/2026", validFrom = "2027-01-01", validTo = "2027-12-31" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("2026", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-01-02", "2026-12-31")]
    [InlineData("2026-01-01", "2026-12-30")]
    [InlineData("2026-03-01", "2026-09-30")]
    public async Task A_certificate_must_run_from_1_January_to_31_December(string from, string to)
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);

        var response = await client.SendAsync(
            HttpMethod.Post,
            "/api/me/practising-certificates",
            new { certificateNumber = "9611/2026", validFrom = from, validTo = to });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dates_are_required()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);

        var response = await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", new { certificateNumber = "9611/2026" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_same_certificate_cannot_be_recorded_twice_by_anyone()
    {
        await using var world = await WorldAsync();
        var other = await world.Factory.CreateMemberAsync("other@example.test", world.Agency, [Role.RegisteredAgent], currentCertificate: false);
        var year = world.Factory.Today.Year;
        var mine = await world.SignInAsync(world.Agent);
        var theirs = await world.SignInAsync(other);

        Assert.Equal(HttpStatusCode.Created, (await mine.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), year))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await mine.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), year))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await theirs.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), year))).StatusCode);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync()));
    }

    [Fact]
    public async Task Association_staff_are_not_members_and_have_no_certificates()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Admin);

        var response = await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), world.Factory.Today.Year));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync()));
    }

    [Fact]
    public async Task Adding_a_certificate_is_audited()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);
        var year = world.Factory.Today.Year;

        await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), year));

        var entry = Assert.Single(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(record => record.Action == "PractisingCertificateAdded").ToListAsync()));
        Assert.Equal(world.Agent.Id, entry.ActorUserId);
        Assert.Equal($"9611/{year}", entry.Subject);
    }

    // --- Without a current certificate: sign in, profile, add one, and nothing else ----------------

    [Fact]
    public async Task Without_a_current_certificate_a_member_can_still_sign_in_see_their_profile_and_add_a_certificate()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent); // signing in works

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode); // the profile
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me/practising-certificates")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), world.Factory.Today.Year))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("RunPreDealCheck")]
    [InlineData("FileConductReport")]
    [InlineData("ViewUpheldCases")]
    [InlineData("RaiseInternalSuspicion")]
    [InlineData("ReadFicReports")]
    public async Task Without_a_current_certificate_nothing_else_works(string capability)
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/probe/can/{capability}")).StatusCode);
    }

    [Fact]
    public async Task Adding_a_certificate_does_not_unlock_anything_until_an_admin_verifies_it()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Agent);
        var created = await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(9611), world.Factory.Today.Year));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        Assert.Equal(VerifyCertificateOutcome.Verified, await VerifyAsync(world.Factory, id, world.Admin.Id));

        // The same session, no new sign-in: the certificate is checked afresh on every request.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
        var profile = await (await client.GetAsync("/api/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(profile.GetProperty("hasCurrentPractisingCertificate").GetBoolean());
    }

    // --- Verifying ----------------------------------------------------------------------------------

    private static async Task<Guid> AddUnverifiedAsync(World world)
    {
        var certificate = await world.Factory.AddCertificateAsync(world.Agent, verified: false);

        return certificate.Id;
    }

    [Fact]
    public async Task An_admin_verifies_a_certificate_and_it_records_who_and_when()
    {
        await using var world = await WorldAsync();
        var id = await AddUnverifiedAsync(world);

        var outcome = await VerifyAsync(world.Factory, id, world.Admin.Id);

        Assert.Equal(VerifyCertificateOutcome.Verified, outcome);
        var stored = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.True(stored.IsVerified);
        Assert.Equal(world.Admin.Id, stored.VerifiedByUserId);
        Assert.True((world.Factory.Clock.GetUtcNow() - stored.VerifiedAt!.Value).Duration() < TimeSpan.FromMilliseconds(1));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task Only_an_admin_can_verify()
    {
        await using var world = await WorldAsync();
        var id = await AddUnverifiedAsync(world);
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var officer = await world.Factory.CreateMemberAsync("officer@example.test", world.Agency, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]);

        Assert.Equal(VerifyCertificateOutcome.NotAnAdmin, await VerifyAsync(world.Factory, id, world.Agent.Id)); // the owner
        Assert.Equal(VerifyCertificateOutcome.NotAnAdmin, await VerifyAsync(world.Factory, id, officer.Id));
        Assert.Equal(VerifyCertificateOutcome.NotAnAdmin, await VerifyAsync(world.Factory, id, reviewer.Id));
        Assert.Equal(VerifyCertificateOutcome.NotAnAdmin, await VerifyAsync(world.Factory, id, Guid.NewGuid())); // nobody
        Assert.False((await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync(c => c.Id == id))).IsVerified);
    }

    [Fact]
    public async Task Verifying_an_unknown_certificate_is_not_found()
    {
        await using var world = await WorldAsync();

        Assert.Equal(VerifyCertificateOutcome.NotFound, await VerifyAsync(world.Factory, Guid.NewGuid(), world.Admin.Id));
    }

    [Fact]
    public async Task A_certificate_cannot_be_verified_twice_and_the_first_verification_stands()
    {
        await using var world = await WorldAsync();
        var id = await AddUnverifiedAsync(world);
        var secondAdmin = await world.Factory.CreateStaffAsync("admin2@example.test", Role.Admin);
        await VerifyAsync(world.Factory, id, world.Admin.Id);

        Assert.Equal(VerifyCertificateOutcome.AlreadyVerified, await VerifyAsync(world.Factory, id, secondAdmin.Id));

        var stored = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(world.Admin.Id, stored.VerifiedByUserId);
    }

    [Fact]
    public async Task Verifying_is_audited_with_who_and_which_certificate()
    {
        await using var world = await WorldAsync();
        var id = await AddUnverifiedAsync(world);

        await VerifyAsync(world.Factory, id, world.Admin.Id);

        var entry = Assert.Single(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(r => r.Action == "PractisingCertificateVerified").ToListAsync()));
        Assert.Equal(world.Admin.Id, entry.ActorUserId);
        Assert.Contains(world.Factory.Today.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_endpoint_for_verifying_yet()
    {
        await using var world = await WorldAsync();
        var id = await AddUnverifiedAsync(world);
        var client = await world.SignInAsync(world.Admin);

        foreach (var path in new[] { $"/api/practising-certificates/{id}/verify", $"/api/admin/practising-certificates/{id}/verify", $"/api/me/practising-certificates/{id}/verify" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Post, path)).StatusCode);
        }
    }

    // --- History is never overwritten ------------------------------------------------------------------

    [Fact]
    public async Task A_new_years_certificate_is_a_new_row_and_the_old_one_is_left_exactly_as_it_was()
    {
        await using var world = await WorldAsync();
        var year = world.Factory.Today.Year;
        var old = await world.Factory.AddCertificateAsync(world.Agent, year - 1);
        var before = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync(c => c.Id == old.Id));
        var client = await world.SignInAsync(world.Agent);

        await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body(world.Number(300, year), year));

        var all = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().OrderBy(c => c.ValidFrom).ToListAsync());
        Assert.Equal(2, all.Count);
        var after = all.Single(c => c.Id == old.Id);
        Assert.Equal(before.Number, after.Number);
        Assert.Equal(before.ValidFrom, after.ValidFrom);
        Assert.Equal(before.ValidTo, after.ValidTo);
        Assert.Equal(before.VerifiedAt, after.VerifiedAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(before.Version, after.Version); // not touched at all
    }

    [Fact]
    public async Task Verifying_one_certificate_does_not_touch_the_others()
    {
        await using var world = await WorldAsync();
        var year = world.Factory.Today.Year;
        var older = await world.Factory.AddCertificateAsync(world.Agent, year - 1, verified: false);
        var newer = await world.Factory.AddCertificateAsync(world.Agent, year, verified: false);

        await VerifyAsync(world.Factory, newer.Id, world.Admin.Id);

        var stored = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().ToListAsync());
        Assert.True(stored.Single(c => c.Id == newer.Id).IsVerified);
        Assert.False(stored.Single(c => c.Id == older.Id).IsVerified);
    }

    // --- The 31 December to 1 January changeover (Zambian time) ------------------------------------------

    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_certificate_for_the_year_ends_at_midnight_in_Zambia_on_31_December()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Agent, 2026);

        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T12:00:00Z"));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id));
        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T21:59:59Z")); // 23:59:59 in Zambia
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id));

        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T22:00:00Z")); // 00:00:00 on 1 January in Zambia
        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
        world.Factory.Clock.SetUtcNow(Utc("2027-01-01T12:00:00Z"));
        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task With_both_years_verified_access_carries_straight_over_the_new_year()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Agent, 2026);
        await world.Factory.AddCertificateAsync(world.Agent, 2027);

        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T21:59:59Z"));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id)); // 2026's
        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T22:00:00Z"));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id)); // 2027's has just begun

        world.Factory.Clock.SetUtcNow(Utc("2027-12-31T21:59:59Z"));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id));
        world.Factory.Clock.SetUtcNow(Utc("2027-12-31T22:00:00Z")); // and nothing for 2028
        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task A_certificate_for_next_year_does_not_count_until_1_January_in_Zambia()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Agent, 2027);

        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T21:59:59Z"));
        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T22:00:00Z"));
        Assert.True(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task An_unverified_certificate_does_not_start_counting_on_1_January()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Agent, 2027, verified: false);

        world.Factory.Clock.SetUtcNow(Utc("2027-01-01T12:00:00Z"));

        Assert.False(await HasCurrentAsync(world.Factory, world.Agent.Id));
    }

    [Fact]
    public async Task The_policy_check_changes_at_the_same_moment_even_in_the_middle_of_a_session()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Agent, 2026);
        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T21:30:00Z"));
        var client = await world.SignInAsync(world.Agent);

        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T21:59:59Z"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        // One second later it is 1 January in Zambia. The session is the same one; the access is not.
        world.Factory.Clock.SetUtcNow(Utc("2026-12-31T22:00:00Z"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        // And the member can still see their profile and add the new year's certificate.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.SendAsync(HttpMethod.Post, "/api/me/practising-certificates", World.Body("5000/2027", 2027))).StatusCode);
    }

    [Fact]
    public async Task Staff_are_not_affected_by_certificates_expiring()
    {
        await using var world = await WorldAsync();
        world.Factory.Clock.SetUtcNow(Utc("2027-06-01T10:00:00Z"));
        var client = await world.SignInAsync(world.Admin);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/ApproveAgents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
    }

    [Fact]
    public async Task The_certificate_is_checked_from_the_database_on_every_request_not_remembered_in_the_session()
    {
        await using var world = await WorldAsync();
        var certificate = await world.Factory.AddCertificateAsync(world.Agent);
        var client = await world.SignInAsync(world.Agent);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        // Taken away behind the application's back (as a later correction might).
        await world.Factory.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE practising_certificates SET verified_at = NULL WHERE id = {certificate.Id}"));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
    }
}
