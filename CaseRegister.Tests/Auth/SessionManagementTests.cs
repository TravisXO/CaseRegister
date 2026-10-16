using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class SessionManagementTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private static async Task<List<JsonElement>> ListAsync(ApiClient client)
    {
        var response = await client.GetAsync("/api/auth/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }

    private static Task<int> SessionCountAsync(ApiFactory factory) => factory.WithDbAsync(db => db.Sessions.CountAsync());

    private static async Task<ApiClient> SignInAsync(ApiFactory factory, ApplicationUser user, string? forwardedFor = null)
    {
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user, forwardedFor: forwardedFor)).StatusCode);

        return client;
    }

    // --- The cookie holds only a session id; the rest is on the server -----------------------

    [Fact]
    public async Task Signing_in_creates_a_server_side_session_whose_ticket_is_protected_and_whose_id_is_hashed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);

        await SignInAsync(factory, user);

        var session = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync());
        Assert.Equal(user.Id, session.UserId);
        Assert.Matches("^[0-9A-F]{64}$", session.SessionHash);

        // The ticket would carry the email and the user id in the clear if it were stored as plain data.
        var ticket = Encoding.UTF8.GetString(session.Ticket);
        Assert.DoesNotContain(Email, ticket, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.Id.ToString(), ticket, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cookie_is_not_the_session_id_that_is_stored_and_not_the_hash_either()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var response = await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user);

        var cookieValue = ApiClient.SetCookieHeader(response, "__Host-cr")!.Split(';')[0].Split('=', 2)[1];
        var session = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync());

        Assert.NotEqual(session.SessionHash, cookieValue);
        Assert.DoesNotContain(session.SessionHash, cookieValue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(user.Id.ToString(), cookieValue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Taking_the_session_row_away_ends_the_session_at_once_whatever_the_cookie_says()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);

        await factory.WithDbAsync(async db => await db.Sessions.ExecuteDeleteAsync());

        // No time has passed, so no timeout or stamp check is involved: it is the server-side row that counts.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    // --- Signing out ----------------------------------------------------------------------------

    [Fact]
    public async Task Signing_out_deletes_the_server_side_session_immediately()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        Assert.Equal(1, await SessionCountAsync(factory));

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out")).StatusCode);

        Assert.Equal(0, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task A_cookie_copied_before_signing_out_is_worthless_afterwards()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = factory.CreateApiClient();
        var signedIn = await client.SignInWithTwoFactorAsync(factory, user);
        var stolen = ApiClient.SetCookieHeader(signedIn, "__Host-cr")!.Split(';')[0];

        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        using var thief = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/probe/members-only", UriKind.Relative));
        request.Headers.Add("Cookie", stolen);

        // The cookie is still a perfectly valid, correctly signed cookie. The session behind it is gone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_of_a_restricted_session_deletes_it_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var client = await TwoFactorFlows.RestrictedSessionAsync(factory, user);
        Assert.Equal(1, await SessionCountAsync(factory));

        await client.SendAsync(HttpMethod.Post, "/api/auth/sign-out");

        Assert.Equal(0, await SessionCountAsync(factory));
    }

    // --- Listing and ending your own sessions ----------------------------------------------------

    [Fact]
    public async Task A_member_sees_their_sessions_and_which_one_is_the_current_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var laptop = await SignInAsync(factory, user, forwardedFor: "198.51.100.10");
        await SignInAsync(factory, user, forwardedFor: "198.51.100.20");

        var sessions = await ListAsync(laptop);

        Assert.Equal(2, sessions.Count);
        var current = Assert.Single(sessions, session => session.GetProperty("isCurrent").GetBoolean());
        Assert.Equal("198.51.100.10", current.GetProperty("ipAddress").GetString());
        Assert.Contains(sessions, session => session.GetProperty("ipAddress").GetString() == "198.51.100.20");
        Assert.All(sessions, session =>
        {
            Assert.NotEqual(Guid.Empty, session.GetProperty("id").GetGuid());
            Assert.True(session.TryGetProperty("signedInAt", out _));
            Assert.True(session.TryGetProperty("lastActiveAt", out _));
        });
    }

    [Fact]
    public async Task The_list_never_includes_anything_that_could_be_used_as_a_cookie()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        var stored = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync());

        var body = await (await client.GetAsync("/api/auth/sessions")).Content.ReadAsStringAsync();

        Assert.DoesNotContain(stored.SessionHash, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_member_can_end_one_of_their_other_sessions_and_it_stops_working_at_once()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var laptop = await SignInAsync(factory, user);
        var phone = await SignInAsync(factory, user);
        var phoneId = (await ListAsync(phone)).Single(session => session.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();

        var response = await laptop.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{phoneId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/probe/members-only")).StatusCode);
        Assert.Single(await ListAsync(laptop));
    }

    [Fact]
    public async Task A_member_can_end_their_current_session_which_signs_them_out()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        var currentId = (await ListAsync(client)).Single().GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{currentId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_end_someone_elses_session_and_it_carries_on()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var alice = await factory.CreateUserAsync("alice@example.test");
        var bob = await factory.CreateUserAsync("bob@example.test");
        var alicesClient = await SignInAsync(factory, alice);
        var bobsClient = await SignInAsync(factory, bob);
        var alicesSession = (await ListAsync(alicesClient)).Single().GetProperty("id").GetGuid();

        var response = await bobsClient.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{alicesSession}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alicesClient.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(2, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task A_member_only_ever_sees_their_own_sessions()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var alice = await factory.CreateUserAsync("alice@example.test");
        var bob = await factory.CreateUserAsync("bob@example.test");
        var alicesClient = await SignInAsync(factory, alice);
        await SignInAsync(factory, bob);
        await SignInAsync(factory, bob);

        Assert.Single(await ListAsync(alicesClient));
    }

    [Fact]
    public async Task Ending_a_session_that_does_not_exist_is_a_404()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);

        var response = await client.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Listing_and_ending_sessions_need_a_fully_signed_in_session()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var anonymous = factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{Guid.NewGuid()}")).StatusCode);

        var halfWay = await TwoFactorFlows.AtTheCodeStepAsync(factory, user);
        Assert.Equal(HttpStatusCode.Unauthorized, (await halfWay.GetAsync("/api/auth/sessions")).StatusCode);
    }

    [Fact]
    public async Task Ending_a_session_is_audited_with_who_and_which()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var laptop = await SignInAsync(factory, user);
        var phone = await SignInAsync(factory, user);
        var phoneId = (await ListAsync(phone)).Single(session => session.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();

        await laptop.SendAsync(HttpMethod.Delete, $"/api/auth/sessions/{phoneId}");

        var entry = Assert.Single(await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(r => r.Action == "SessionRevoked").ToListAsync()));
        Assert.Equal(user.Id, entry.ActorUserId);
        Assert.Equal(phoneId.ToString(), entry.Subject);
    }

    // --- At most three sessions -----------------------------------------------------------------

    [Fact]
    public async Task A_fourth_sign_in_ends_the_oldest_session()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var first = await SignInAsync(factory, user);
        var second = await SignInAsync(factory, user);
        var third = await SignInAsync(factory, user);
        Assert.Equal(3, await SessionCountAsync(factory));

        var fourth = await SignInAsync(factory, user);

        Assert.Equal(3, await SessionCountAsync(factory));
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await third.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fourth.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task Every_further_sign_in_ends_whichever_is_then_the_oldest()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var clients = new List<ApiClient>();
        for (var i = 0; i < 5; i++)
        {
            clients.Add(await SignInAsync(factory, user));
        }

        var alive = new List<bool>();
        foreach (var client in clients)
        {
            alive.Add((await client.GetAsync("/probe/members-only")).StatusCode == HttpStatusCode.OK);
        }

        Assert.Equal([false, false, true, true, true], alive);
    }

    [Fact]
    public async Task The_oldest_session_going_is_audited()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        for (var i = 0; i < 4; i++)
        {
            await SignInAsync(factory, user);
        }

        var entry = Assert.Single(await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(r => r.Action == "SessionRevoked").ToListAsync()));

        Assert.Equal(user.Id, entry.ActorUserId);
        Assert.Contains("limit", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sessions_are_counted_per_user()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var alice = await factory.CreateUserAsync("alice@example.test");
        var bob = await factory.CreateUserAsync("bob@example.test");
        var alicesClient = await SignInAsync(factory, alice);
        for (var i = 0; i < 4; i++)
        {
            await SignInAsync(factory, bob);
        }

        // Bob's busy day cost Alice nothing.
        Assert.Equal(HttpStatusCode.OK, (await alicesClient.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(4, await SessionCountAsync(factory)); // one of Alice's, three of Bob's
    }

    [Fact]
    public async Task Sessions_that_have_already_expired_do_not_count_towards_the_three()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        for (var i = 0; i < 3; i++)
        {
            await SignInAsync(factory, user);
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(31)); // all three idle out
        var fresh = await SignInAsync(factory, user);

        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/probe/members-only")).StatusCode);
        Assert.Single(await ListAsync(fresh));
        Assert.Equal(1, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task A_sign_in_removes_every_expired_session_whoever_it_belonged_to_and_keeps_the_live_ones()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var gone = await factory.CreateUserAsync("gone@example.test");
        var live = await factory.CreateUserAsync("live@example.test");
        var arriving = await factory.CreateUserAsync("arriving@example.test");
        await SignInAsync(factory, gone);
        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        await SignInAsync(factory, live);

        // Nobody asks for it, so expiring on its own removes nothing: no scheduler runs here.
        factory.Clock.Advance(TimeSpan.FromMinutes(20)); // the first session is now 40 minutes old, the second 20
        Assert.Equal(2, await SessionCountAsync(factory));

        await SignInAsync(factory, arriving);

        var remaining = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().Select(session => session.UserId).ToListAsync());
        Assert.Equal(new[] { live.Id, arriving.Id }.Order(), remaining.Order());
    }

    [Fact]
    public async Task A_restricted_session_counts_towards_the_limit_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email, twoFactor: false);
        var sessions = new List<ApiClient>();
        for (var i = 0; i < 4; i++)
        {
            sessions.Add(await TwoFactorFlows.RestrictedSessionAsync(factory, user));
        }

        Assert.Equal(3, await SessionCountAsync(factory));
        Assert.Equal(HttpStatusCode.Unauthorized, (await sessions[0].GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sessions[3].GetAsync("/api/auth/session")).StatusCode);
    }

    // --- Expiry and renewal against the store ---------------------------------------------------

    [Fact]
    public async Task An_expired_session_is_refused_and_its_row_is_removed()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);

        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(0, await SessionCountAsync(factory));
    }

    [Fact]
    public async Task Activity_renews_the_session_in_the_store_and_the_last_active_time_moves_on()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        var before = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync());

        factory.Clock.Advance(TimeSpan.FromMinutes(20)); // past halfway: the sliding window renews
        await client.GetAsync("/probe/members-only");

        var after = await factory.WithDbAsync(db => db.Sessions.AsNoTracking().SingleAsync());
        Assert.True(after.ExpiresAt > before.ExpiresAt);
        Assert.True(after.LastActiveAt > before.LastActiveAt);
        Assert.Equal(before.Id, after.Id);
    }

    [Fact]
    public async Task The_session_id_and_the_fully_signed_in_status_survive_the_five_minute_stamp_refresh()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync(Email);
        var client = await SignInAsync(factory, user);
        var idBefore = (await ListAsync(client)).Single().GetProperty("id").GetGuid();

        // The security stamp is re-read every 5 minutes and the claims rebuilt from the database.
        for (var i = 0; i < 4; i++)
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
        }

        var after = (await ListAsync(client)).Single();
        Assert.Equal(idBefore, after.GetProperty("id").GetGuid());
        Assert.True(after.GetProperty("isCurrent").GetBoolean());
    }
}
