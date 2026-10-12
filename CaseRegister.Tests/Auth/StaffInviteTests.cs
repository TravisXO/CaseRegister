using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

/// <summary>Staff invites: two admins create the account, the invited person finishes it from a one-time link.</summary>
[Collection(PostgresTestGroup.Name)]
public class StaffInviteTests(PostgresFixture postgres)
{
    private const string InviteeEmail = "new.person@example.test";
    private const string InviteeName = "New Person";
    private const string Setup = "/api/auth/staff-setup";

    private sealed record World(ApiFactory Factory, ApplicationUser A, ApplicationUser B, ApplicationUser C) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public Task<ApplicationUser> Invitee(string email = InviteeEmail) => Factory.GetUserAsync(email);

        public Task<int> AuditCount(string action) => Factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action));

        /// <summary>Invited by A, confirmed by B. Returns the one-time link and its token.</summary>
        public async Task<(string Link, string Token)> InviteAsync(string email = InviteeEmail, string name = InviteeName, Role role = Role.CaseReviewer)
        {
            var proposal = await Factory.ProposeOkAsync(A, Proposals.Invite(email, name, role));
            var confirmed = await Factory.ConfirmWithLinkAsync(B, proposal);

            return (confirmed.SetupLink!, ApprovalFlows.TokenOf(confirmed.SetupLink!));
        }

        public static async Task<(HttpResponseMessage Start, string? SharedKey)> StartAsync(ApiClient client, string token, string? forwardedFor = null)
        {
            var response = await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token }, forwardedFor: forwardedFor);
            var key = response.StatusCode == HttpStatusCode.OK ? (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString() : null;

            return (response, key);
        }

        public async Task<HttpResponseMessage> CompleteAsync(ApiClient client, string token, string? sharedKey, string? password = null, string? code = null, string? forwardedFor = null, string email = InviteeEmail)
        {
            if (sharedKey is not null)
            {
                Factory.RememberTwoFactorKey(await Invitee(email), Base32.Decode(sharedKey));
            }

            var twoFactorCode = code ?? (sharedKey is null ? "123456" : Factory.NextTotpCode(await Invitee(email)));

            return await client.SendAsync(HttpMethod.Post, $"{Setup}/complete", new { token, password = password ?? SignUps.StrongPassword, twoFactorCode }, forwardedFor: forwardedFor);
        }

        /// <summary>The whole setup, as the invited person does it.</summary>
        public async Task<HttpResponseMessage> FinishSetupAsync(string token, string email = InviteeEmail, string? password = null)
        {
            var client = Factory.CreateApiClient();
            var (start, key) = await StartAsync(client, token);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);

            return await CompleteAsync(client, token, key, password, email: email);
        }
    }

    private async Task<World> WorldAsync(int admins = 3, int staffSetupPerIpPerMinute = 10_000)
    {
        var factory = await ApiFactory.CreateAsync(postgres, staffSetupPerIpPerMinute: staffSetupPerIpPerMinute);
        var people = await factory.CreateAdminsAsync(admins);

        return new World(factory, people[0], people[1], people.Length > 2 ? people[2] : people[1]);
    }

    private static string[] Codes(IEnumerable<ApprovalViolation>? problems) => [.. (problems ?? []).Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    // --- Proposing and confirming an invite -------------------------------------------------------------------

    [Fact]
    public async Task Nothing_is_created_until_a_different_admin_confirms_and_the_confirmer_sees_who_and_in_what_role()
    {
        await using var world = await WorldAsync();

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite("New.Person@Example.TEST", InviteeName, Role.Admin));

        Assert.Null(await world.Factory.WithDbAsync(db => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == InviteeEmail)));
        Assert.Equal(InviteeEmail, proposal.TargetEmail); // normalised
        Assert.Equal(InviteeName, proposal.TargetName);
        Assert.Null(proposal.TargetUserId);
        Assert.Equal("{\"email\":\"new.person@example.test\",\"fullName\":\"New Person\",\"role\":\"Admin\"}", proposal.Payload);
        Assert.Contains("Invite New Person (new.person@example.test) as Admin", proposal.Description, StringComparison.Ordinal);
        Assert.Equal(ConfirmOutcome.NotAllowed, (await world.Factory.ConfirmAsync(world.A, proposal)).Outcome);

        await world.Factory.ConfirmOkAsync(world.B, proposal);

        Assert.Equal([Role.Admin], await world.Factory.RolesOfAsync(await world.Invitee()));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.CaseReviewer)]
    public async Task Confirming_creates_an_account_with_its_role_that_cannot_sign_in_yet(Role role)
    {
        await using var world = await WorldAsync();

        await world.InviteAsync(role: role);

        var invitee = await world.Invitee();
        Assert.Equal(AccountStatus.Pending, invitee.AccountStatus);
        Assert.Null(invitee.PasswordHash); // no password exists to sign in with
        Assert.False(invitee.TwoFactorEnabled);
        Assert.Null(invitee.AgencyId);
        Assert.Null(invitee.MembershipNumber);
        Assert.Equal(InviteeName, invitee.FullName);
        Assert.Equal([role], await world.Factory.RolesOfAsync(invitee));
        foreach (var guess in new[] { SignUps.StrongPassword, "password", "correct horse battery staple" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().SignInAsync(InviteeEmail, guess)).StatusCode);
        }
    }

    [Theory]
    [InlineData(Role.RegisteredAgent)]
    [InlineData(Role.AgencyComplianceOfficer)]
    public async Task Agent_roles_cannot_be_invited_because_agents_apply_and_are_approved(Role role)
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, role));

        Assert.Equal(ProposeOutcome.Invalid, result.Outcome);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task An_invite_needs_a_valid_address_a_name_and_nothing_else()
    {
        await using var world = await WorldAsync();
        var reason = Proposals.Reason;

        foreach (var bad in new[]
        {
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, Role.Admin, null, reason, InviteEmail: "not-an-email", InviteFullName: "Name"),
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, Role.Admin, null, reason, InviteEmail: InviteeEmail, InviteFullName: " "),
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, Role.Admin, null, reason, InviteEmail: null, InviteFullName: "Name"),
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, null, null, reason, InviteEmail: InviteeEmail, InviteFullName: "Name"),
            new ProposeActionCommand(PendingActionType.InviteStaff, world.B.Id, null, Role.Admin, null, reason, InviteEmail: InviteeEmail, InviteFullName: "Name"),
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, Role.Admin, null, reason, Guid.NewGuid(), InviteEmail: InviteeEmail, InviteFullName: "Name"),
            new ProposeActionCommand(PendingActionType.InviteStaff, null, null, Role.Admin, null, reason, InviteEmail: InviteeEmail, InviteFullName: new string('a', 201)),
            new ProposeActionCommand(PendingActionType.AssignRole, world.B.Id, null, Role.Admin, null, reason, InviteEmail: InviteeEmail), // an invite parameter on something else
        })
        {
            Assert.Equal(ProposeOutcome.Invalid, (await world.Factory.ProposeAsync(world.A, bad)).Outcome);
        }
    }

    [Fact]
    public async Task Inviting_an_email_that_already_has_an_account_is_refused_with_a_clear_message()
    {
        await using var world = await WorldAsync();
        var agency = await world.Factory.CreateAgencyAsync();
        await world.Factory.CreateMemberAsync("agent@example.test", agency, [Role.RegisteredAgent]);
        await SignUps.ApplyAsync(world.Factory, "applicant@example.test");

        foreach (var email in new[] { "agent@example.test", "AGENT@example.test", world.B.Email!, "applicant@example.test", "ADMIN1@EXAMPLE.test" })
        {
            var result = await world.Factory.ProposeAsync(world.A, Proposals.Invite(email, "Someone", Role.Admin));

            Assert.Equal(ProposeOutcome.Refused, result.Outcome);
            Assert.Equal(["EmailAlreadyExists"], Codes(result.Problems));
            Assert.Equal("An account with this email address already exists.", result.Problems![0].Message);
        }

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task The_clear_message_reaches_the_admin_over_http()
    {
        await using var world = await WorldAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.A);

        var response = await client.SendAsync(HttpMethod.Post, "/api/admin/pending-actions", new { type = "InviteStaff", role = "Admin", inviteEmail = world.B.Email, inviteFullName = "Someone", reason = Proposals.Reason });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("An account with this email address already exists.", problem.GetProperty("problems")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_account_that_appears_between_the_proposal_and_the_confirmation_stops_the_invite()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.CaseReviewer));
        await world.Factory.CreateUserAsync(InviteeEmail);

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["EmailAlreadyExists"], Codes(result.Problems));
        Assert.Null(result.SetupLink);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.StaffSetupTokens.CountAsync()));
    }

    [Fact]
    public async Task One_invite_per_address_at_a_time_and_it_can_be_withdrawn_or_lapse()
    {
        await using var world = await WorldAsync();
        var first = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.Admin));

        Assert.Equal(ProposeOutcome.AlreadyPending, (await world.Factory.ProposeAsync(world.C, Proposals.Invite("NEW.PERSON@example.test", "Other Name", Role.CaseReviewer))).Outcome);
        Assert.Equal(ProposeOutcome.Proposed, (await world.Factory.ProposeAsync(world.C, Proposals.Invite("someone.else@example.test", "Someone Else", Role.Admin))).Outcome);

        await world.Factory.WithdrawAsync(world.A, first, "The wrong person, sorry.");
        Assert.Equal(ConfirmOutcome.NotPending, (await world.Factory.ConfirmAsync(world.B, first)).Outcome);
        var second = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.Admin));

        world.Factory.Clock.Advance(TimeSpan.FromHours(72));
        Assert.Equal(ConfirmOutcome.Expired, (await world.Factory.ConfirmAsync(world.B, second)).Outcome);
        Assert.Null(await world.Factory.WithDbAsync(db => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == InviteeEmail)));
    }

    // --- The setup link --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_link_is_the_setup_page_then_a_fragment_so_the_token_never_reaches_a_server_log()
    {
        await using var world = await WorldAsync();

        var (link, token) = await world.InviteAsync();

        Assert.StartsWith("https://app.example.test/staff/setup#", link, StringComparison.Ordinal);
        var uri = new Uri(link);
        Assert.Equal(string.Empty, uri.Query); // nothing in the query string either
        Assert.Equal("/staff/setup", uri.AbsolutePath);
        Assert.Equal("#" + token, uri.Fragment);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", token); // 256 bits
        Assert.DoesNotContain(token, uri.GetLeftPart(UriPartial.Query), StringComparison.Ordinal); // what a server would be sent
    }

    [Fact]
    public async Task The_link_is_shown_once_to_the_confirming_admin_only()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.CaseReviewer));

        var first = await world.Factory.ConfirmAsync(world.B, proposal);
        var repeat = await world.Factory.ConfirmAsync(world.B, proposal, code: "000000");

        Assert.NotNull(first.SetupLink);
        Assert.False(first.WasRepeat);
        Assert.True(repeat.WasRepeat);
        Assert.Null(repeat.SetupLink); // it cannot be shown again: only a hash is kept
        var token = ApprovalFlows.TokenOf(first.SetupLink!);

        // Nobody else, in any view, sees it.
        var seen = JsonSerializer.Serialize(await world.Factory.GetAsync(proposal.Id)) + JsonSerializer.Serialize(await world.Factory.ListAsync(includeClosed: true));
        Assert.DoesNotContain(token, seen, StringComparison.Ordinal);
        Assert.Equal(ConfirmOutcome.NotPending, (await world.Factory.ConfirmAsync(world.C, proposal)).Outcome);
    }

    [Fact]
    public async Task The_link_is_in_the_confirm_response_over_http_and_not_in_a_repeat_or_the_list()
    {
        await using var world = await WorldAsync();
        var maker = world.Factory.CreateApiClient();
        var checker = world.Factory.CreateApiClient();
        await maker.SignInWithTwoFactorAsync(world.Factory, world.A);
        await checker.SignInWithTwoFactorAsync(world.Factory, world.B);
        var proposed = await maker.SendAsync(HttpMethod.Post, "/api/admin/pending-actions", new { type = "InviteStaff", role = "CaseReviewer", inviteEmail = InviteeEmail, inviteFullName = InviteeName, reason = Proposals.Reason });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = await proposed.Content.ReadFromJsonAsync<JsonElement>();
        var id = proposal.GetProperty("id").GetGuid();

        var confirm = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/confirm", new { payloadHash = proposal.GetProperty("payloadHash").GetString(), twoFactorCode = world.Factory.NextTotpCode(world.B) });
        var again = await checker.SendAsync(HttpMethod.Post, $"/api/admin/pending-actions/{id}/confirm", new { payloadHash = proposal.GetProperty("payloadHash").GetString(), twoFactorCode = "000000" });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var link = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("setupLink").GetString()!;
        Assert.Contains('#', link);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.False((await again.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("setupLink", out _));
        var seenByMaker = await (await maker.GetAsync($"/api/admin/pending-actions/{id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApprovalFlows.TokenOf(link), seenByMaker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_hash_of_the_token_is_stored_it_lasts_a_day_and_the_token_is_nowhere_else()
    {
        await using var world = await WorldAsync();
        var confirmedAt = world.Factory.Clock.GetUtcNow();

        var (_, token) = await world.InviteAsync();

        var stored = await world.Factory.WithDbAsync(db => db.StaffSetupTokens.AsNoTracking().SingleAsync());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.True((stored.ExpiresAt - confirmedAt - TimeSpan.FromHours(24)).Duration() < TimeSpan.FromMinutes(2));
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.RevokedAt);
        Assert.Equal(world.B.Id, stored.CreatedByUserId); // the admin who confirmed, and so saw the link

        var everywhere = string.Join('|', await world.Factory.WithDbAsync(async db =>
            (await db.AuditLog.AsNoTracking().Select(e => e.Subject + "|" + e.Detail).ToListAsync())
            .Concat(await db.PendingActions.AsNoTracking().Select(a => a.Payload + "|" + a.PayloadHash + "|" + a.Reason + "|" + a.TargetEmail).ToListAsync())
            .Concat(await db.StaffSetupTokens.AsNoTracking().Select(t => t.TokenHash).ToListAsync())
            .Concat(await db.Users.AsNoTracking().Select(u => u.SecurityStamp + "|" + u.ConcurrencyStamp + "|" + u.PasswordHash).ToListAsync())
            .ToList()));
        Assert.DoesNotContain(token, everywhere, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Factory.Logs.Entries, entry => entry.Message.Contains(token, StringComparison.Ordinal));
    }

    // --- Finishing setup ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_password_and_enrolling_in_two_factor_together_makes_the_account_usable()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync(role: Role.Admin);
        var client = world.Factory.CreateApiClient();

        var (start, key) = await World.StartAsync(client, token);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(Totp.KeyBytes, Base32.Decode(key!).Length); // a key for an authenticator app
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().SignInAsync(InviteeEmail, SignUps.StrongPassword)).StatusCode); // still not usable

        var done = await world.CompleteAsync(client, token, key);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(10, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").GetArrayLength());
        var invitee = await world.Invitee();
        Assert.Equal(AccountStatus.Approved, invitee.AccountStatus);
        Assert.NotNull(invitee.PasswordHash);
        Assert.True(invitee.TwoFactorEnabled);
        Assert.NotNull((await world.Factory.WithDbAsync(db => db.Authenticators.AsNoTracking().SingleAsync(a => a.UserId == invitee.Id))).EnabledAt);
        Assert.NotNull((await world.Factory.WithDbAsync(db => db.StaffSetupTokens.AsNoTracking().SingleAsync())).UsedAt);

        // They sign in the ordinary way, with the second factor, and are an admin.
        var session = world.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await session.SignInWithTwoFactorAsync(world.Factory, invitee, SignUps.StrongPassword)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/api/admin/pending-actions")).StatusCode);
        Assert.Equal(1, await world.AuditCount("StaffSetupCompleted"));
    }

    [Fact]
    public async Task A_new_admin_counts_towards_the_two_admin_minimum_only_once_setup_is_complete()
    {
        await using var world = await WorldAsync(admins: 2);
        var third = await world.InviteAsync("third.admin@example.test", "Third Admin", Role.Admin);

        // Invited and created, but not set up: not an active admin, so the minimum still protects the two.
        var before = await world.Factory.ProposeAsync(world.A, Proposals.ForUser(PendingActionType.SuspendUser, world.B));
        Assert.Equal(ProposeOutcome.Refused, before.Outcome);
        Assert.Equal([ApprovalRules.MinimumAdmins], Codes(before.Problems));

        await world.FinishSetupAsync(third.Token, "third.admin@example.test");

        var after = await world.Factory.ProposeAsync(world.A, Proposals.ForUser(PendingActionType.SuspendUser, world.B));
        Assert.Equal(ProposeOutcome.Proposed, after.Outcome);
    }

    [Fact]
    public async Task A_link_works_once()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync();
        Assert.Equal(HttpStatusCode.OK, (await world.FinishSetupAsync(token)).StatusCode);
        var invitee = await world.Invitee();
        var stamp = invitee.SecurityStamp;
        var hash = invitee.PasswordHash;

        var client = world.Factory.CreateApiClient();
        var start = await World.StartAsync(client, token);
        var complete = await world.CompleteAsync(client, token, null, "another very long password");

        Assert.Equal(HttpStatusCode.BadRequest, start.Start.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);
        var after = await world.Invitee();
        Assert.Equal(hash, after.PasswordHash); // the account was not touched again
        Assert.Equal(stamp, after.SecurityStamp);
        Assert.True(await world.Factory.WithDbAsync(db => db.AuditLog.AnyAsync(e => e.Action == "StaffSetupFailed" && e.Detail!.Contains("already used"))));
    }

    [Fact]
    public async Task Two_simultaneous_attempts_with_one_link_set_it_up_once()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync();
        var one = world.Factory.CreateApiClient();
        var two = world.Factory.CreateApiClient();
        var (_, keyOne) = await World.StartAsync(one, token);
        var (_, keyTwo) = await World.StartAsync(two, token); // the second start replaces the first key
        var invitee = await world.Invitee();
        world.Factory.RememberTwoFactorKey(invitee, Base32.Decode(keyTwo!));
        var code = world.Factory.NextTotpCode(invitee);
        var gate = new TaskCompletionSource();

        Task<HttpResponseMessage> Complete(ApiClient client, string password) => Task.Run(async () =>
        {
            await gate.Task;

            return await client.SendAsync(HttpMethod.Post, $"{Setup}/complete", new { token, password, twoFactorCode = code });
        });

        var first = Complete(one, "purple tractor sings loudly");
        var second = Complete(two, "orange ladder hums quietly");
        gate.SetResult();
        var statuses = (await Task.WhenAll(first, second)).Select(response => response.StatusCode).Order().ToArray();

        Assert.NotEqual(keyOne, keyTwo);
        Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.OK));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.StaffSetupTokens.CountAsync(t => t.UsedAt != null)));
        Assert.Equal(1, await world.AuditCount("StaffSetupCompleted"));
    }

    [Fact]
    public async Task A_link_stops_working_after_twenty_four_hours_to_the_second()
    {
        await using var world = await WorldAsync();
        var (_, early) = await world.InviteAsync("early@example.test", "Early Person");
        var (_, late) = await world.InviteAsync("late@example.test", "Late Person");
        var lateExpiry = (await world.Factory.WithDbAsync(db => db.StaffSetupTokens.AsNoTracking().ToListAsync())).Max(t => t.ExpiresAt);

        world.Factory.Clock.SetUtcNow(lateExpiry - TimeSpan.FromSeconds(1)); // still valid, for the later one
        Assert.Equal(HttpStatusCode.OK, (await world.FinishSetupAsync(late, "late@example.test")).StatusCode);

        world.Factory.Clock.SetUtcNow(lateExpiry); // expired, for both
        var client = world.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await World.StartAsync(client, early)).Start.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await world.CompleteAsync(client, early, null)).StatusCode);

        Assert.Equal(AccountStatus.Pending, (await world.Invitee("early@example.test")).AccountStatus);
        Assert.True(await world.Factory.WithDbAsync(db => db.AuditLog.AnyAsync(e => e.Action == "StaffSetupFailed" && e.Detail!.Contains("expired"))));
    }

    [Fact]
    public async Task Unknown_used_and_expired_links_all_get_exactly_the_same_answer()
    {
        await using var world = await WorldAsync();
        var (_, used) = await world.InviteAsync("used@example.test", "Used Person");
        await world.FinishSetupAsync(used, "used@example.test");
        var (_, expiring) = await world.InviteAsync("expiring@example.test", "Expiring Person");
        world.Factory.Clock.Advance(TimeSpan.FromHours(25));
        var client = world.Factory.CreateApiClient();
        var answers = new List<(HttpStatusCode, string?, string?)>();

        foreach (var token in new[] { "not-a-token", new string('A', 43), used, expiring })
        {
            var start = await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token });
            var complete = await client.SendAsync(HttpMethod.Post, $"{Setup}/complete", new { token, password = SignUps.StrongPassword, twoFactorCode = "123456" });
            answers.Add(await Describe(start));
            answers.Add(await Describe(complete));
        }

        Assert.All(answers, answer => Assert.Equal(answers[0], answer));
        Assert.Equal(HttpStatusCode.BadRequest, answers[0].Item1);
    }

    /// <summary>What the caller can see: the status and the words. (The trace id differs on every response.)</summary>
    private static async Task<(HttpStatusCode, string?, string?)> Describe(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (response.StatusCode, body.GetProperty("title").GetString(), body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Failed_attempts_are_logged_without_the_token()
    {
        await using var world = await WorldAsync();
        var client = world.Factory.CreateApiClient();

        await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token = "guess-number-one" }, forwardedFor: "203.0.113.9");

        var entry = Assert.Single(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "StaffSetupFailed").ToListAsync()));
        Assert.Contains("unknown token", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("failures from this address this hour: 1", entry.Detail, StringComparison.Ordinal);
        Assert.Equal("203.0.113.9", entry.IpAddress);
        Assert.DoesNotContain("guess-number-one", entry.Detail + entry.Subject, StringComparison.Ordinal);
    }

    // --- Setup can do two things and nothing else ----------------------------------------------------------------

    [Theory]
    [InlineData("role", "Admin")]
    [InlineData("roles", "Admin")]
    [InlineData("email", "someone.else@example.test")]
    [InlineData("fullName", "Someone Else")]
    [InlineData("accountStatus", "Approved")]
    [InlineData("agencyId", "6f6f6f6f-6f6f-6f6f-6f6f-6f6f6f6f6f6f")]
    [InlineData("membershipNumber", "AGT 1")]
    [InlineData("userId", "6f6f6f6f-6f6f-6f6f-6f6f-6f6f6f6f6f6f")]
    [InlineData("twoFactorEnabled", "true")]
    public async Task A_setup_request_that_asks_for_anything_beyond_password_and_two_factor_is_turned_away(string field, string value)
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync(role: Role.CaseReviewer);
        var client = world.Factory.CreateApiClient();
        var (_, key) = await World.StartAsync(client, token);
        var invitee = await world.Invitee();
        world.Factory.RememberTwoFactorKey(invitee, Base32.Decode(key!));
        var before = invitee;

        var complete = new Dictionary<string, object?>
        {
            ["token"] = token,
            ["password"] = SignUps.StrongPassword,
            ["twoFactorCode"] = world.Factory.NextTotpCode(invitee),
            [field] = value,
        };
        var start = new Dictionary<string, object?> { ["token"] = token, [field] = value };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/complete", complete)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/start", start)).StatusCode);

        // Nothing happened: not the account, not its roles, not the link.
        var after = await world.Invitee();
        Assert.Equal(AccountStatus.Pending, after.AccountStatus);
        Assert.Null(after.PasswordHash);
        Assert.Equal(before.FullName, after.FullName);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal([Role.CaseReviewer], await world.Factory.RolesOfAsync(after));
        Assert.Null((await world.Factory.WithDbAsync(db => db.StaffSetupTokens.AsNoTracking().SingleAsync())).UsedAt);

        // And the same link still does its two things.
        Assert.Equal(HttpStatusCode.OK, (await world.CompleteAsync(client, token, null, code: world.Factory.NextTotpCode(invitee))).StatusCode);
    }

    [Fact]
    public async Task The_password_rules_apply_and_a_refused_password_or_code_leaves_the_link_usable()
    {
        await using var world = await WorldAsync();
        world.Factory.Breaches.BreachedPasswords.Add("password1234567");
        var (_, token) = await world.InviteAsync();
        var client = world.Factory.CreateApiClient();

        // Before the key has been asked for there is nothing to confirm.
        Assert.Equal(HttpStatusCode.Conflict, (await world.CompleteAsync(client, token, null)).StatusCode);

        var (_, key) = await World.StartAsync(client, token);
        foreach (var weak in new[] { "short", "password1234567", "new.person@example.test!" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await world.CompleteAsync(client, token, key, weak)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await world.CompleteAsync(client, token, key, code: "000000")).StatusCode);
        Assert.Equal(AccountStatus.Pending, (await world.Invitee()).AccountStatus);

        Assert.Equal(HttpStatusCode.OK, (await world.CompleteAsync(client, token, key)).StatusCode);
    }

    // --- Limits ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_setup_endpoints_are_rate_limited_per_address()
    {
        await using var world = await WorldAsync(staffSetupPerIpPerMinute: 3);
        var client = world.Factory.CreateApiClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            statuses.Add((await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token = "nothing" }, forwardedFor: "203.0.113.20")).StatusCode);
        }

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token = "nothing" }, forwardedFor: "203.0.113.21")).StatusCode);
    }

    [Fact]
    public async Task Too_many_failed_tokens_from_one_address_block_even_a_valid_link_from_there_for_an_hour()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync();
        var client = world.Factory.CreateApiClient();

        for (var attempt = 0; attempt < SetupFailureTracker.MaxFailuresPerHour; attempt++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token = $"guess-{attempt}" }, forwardedFor: "203.0.113.30")).StatusCode);
        }

        // Now that address is turned away whatever it sends...
        Assert.Equal(HttpStatusCode.TooManyRequests, (await World.StartAsync(client, token, "203.0.113.30")).Start.StatusCode);
        // ...but the invited person, from somewhere else, is not affected...
        var elsewhere = await World.StartAsync(client, token, "203.0.113.31");
        Assert.Equal(HttpStatusCode.OK, elsewhere.Start.StatusCode);

        // ...and the block ends after the hour.
        world.Factory.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await World.StartAsync(client, token, "203.0.113.30")).Start.StatusCode);
    }

    [Fact]
    public async Task The_setup_endpoints_need_the_anti_forgery_token()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync();
        var client = world.Factory.CreateApiClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/start", new { token }, withCsrfToken: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(HttpMethod.Post, $"{Setup}/complete", new { token, password = SignUps.StrongPassword, twoFactorCode = "123456" }, withCsrfToken: false)).StatusCode);
    }

    // --- A lost link ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_lost_link_means_a_new_invite_which_replaces_the_old_one()
    {
        await using var world = await WorldAsync();
        var (_, lost) = await world.InviteAsync(role: Role.CaseReviewer);

        // There is no way to ask for the link again: it can only be replaced by a whole new invite, with two admins.
        var (newLink, replacement) = await world.InviteAsync(name: "New Person Renamed", role: Role.Admin);

        Assert.NotEqual(lost, replacement);
        var client = world.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await World.StartAsync(client, lost)).Start.StatusCode); // the lost one is dead
        Assert.True(await world.Factory.WithDbAsync(db => db.AuditLog.AnyAsync(e => e.Action == "StaffSetupFailed" && e.Detail!.Contains("replaced by a newer invite"))));
        Assert.NotEmpty(newLink);

        var invitee = await world.Invitee();
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Users.CountAsync(u => u.Email == InviteeEmail))); // the same account, not a second
        Assert.Equal("New Person Renamed", invitee.FullName);
        Assert.Equal([Role.Admin], await world.Factory.RolesOfAsync(invitee)); // as the new invite says
        Assert.Equal(2, await world.Factory.WithDbAsync(db => db.StaffSetupTokens.CountAsync()));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.StaffSetupTokens.CountAsync(t => t.RevokedAt != null)));

        Assert.Equal(HttpStatusCode.OK, (await world.FinishSetupAsync(replacement)).StatusCode);
    }

    [Fact]
    public async Task Once_setup_is_complete_the_address_is_taken_and_cannot_be_invited_again()
    {
        await using var world = await WorldAsync();
        var (_, token) = await world.InviteAsync();
        await world.FinishSetupAsync(token);

        var result = await world.Factory.ProposeAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.Admin));

        Assert.Equal(ProposeOutcome.Refused, result.Outcome);
        Assert.Equal(["EmailAlreadyExists"], Codes(result.Problems));
    }

    // --- The log ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_the_invite_is_logged_with_both_steps_and_no_address_in_the_clear()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Invite(InviteeEmail, InviteeName, Role.Admin));
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var entries = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        var proposed = Assert.Single(entries, e => e.Action == "ActionProposed");
        var created = Assert.Single(entries, e => e.Action == "StaffInviteCreated");
        var confirmed = Assert.Single(entries, e => e.Action == "ActionConfirmed");

        Assert.Equal(world.A.Id, proposed.ActorUserId);
        Assert.Equal(world.B.Id, confirmed.ActorUserId);
        Assert.Equal(world.B.Id, created.ActorUserId);
        Assert.Contains("before: no account", confirmed.Detail, StringComparison.Ordinal);
        Assert.Contains("with role Admin", confirmed.Detail, StringComparison.Ordinal);
        foreach (var entry in new[] { proposed, created, confirmed })
        {
            Assert.StartsWith("email-sha256:", entry.Subject, StringComparison.Ordinal);
            Assert.DoesNotContain(InviteeEmail, entry.Subject + entry.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --- Configuration ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/staff/setup")]
    [InlineData("http://app.example.test/staff/setup")] // plain http, and not localhost
    [InlineData("https://app.example.test/staff/setup#already")] // the token needs the fragment to itself
    public void A_bad_setup_page_address_stops_the_application_starting(string url)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Invites:SetupPageUrl"] = url);

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("Invites:SetupPageUrl", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://app.example.test/staff/setup", true)]
    [InlineData("http://localhost:5173/staff/setup", true)]
    [InlineData("http://127.0.0.1:5173/staff/setup", true)]
    [InlineData("http://app.example.test/staff/setup", false)]
    [InlineData("ftp://app.example.test/setup", false)]
    [InlineData("https://app.example.test/setup#x", false)]
    [InlineData(null, false)]
    public void Only_an_https_address_or_a_local_one_with_no_fragment_will_do(string? url, bool valid)
    {
        Assert.Equal(valid, StaffInviteOptions.IsValid(url));
    }

    [Fact]
    public void The_link_is_the_address_then_a_hash_then_the_token()
    {
        var options = new StaffInviteOptions { SetupPageUrl = "https://app.example.test/staff/setup" };

        Assert.Equal("https://app.example.test/staff/setup#abc123", options.LinkFor("abc123"));
    }
}
