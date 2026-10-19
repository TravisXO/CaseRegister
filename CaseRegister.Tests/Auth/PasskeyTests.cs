using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>
/// Passkeys, with a software authenticator standing in for the browser and the phone: adding one, signing in with it, using it as a step-up,
/// refusing one that did not verify the user, and what a reset and a removal leave behind.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public class PasskeyTests(PostgresFixture postgres)
{
    private const string Email = "member@example.test";

    private sealed record Member(ApiFactory Factory, ApiClient Client, ApplicationUser User) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    private async Task<Member> SignedInAsync(string email = Email, int signInPerIpPerMinute = 10_000, bool staff = false)
    {
        var factory = await ApiFactory.CreateAsync(postgres, signInPerIpPerMinute: signInPerIpPerMinute);
        var user = await factory.CreateUserAsync(email, staff: staff);
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);

        return new Member(factory, client, user);
    }

    private static async Task<string> BodyAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private static async Task<string> OptionsAsync(ApiClient client, string path)
    {
        var response = await client.SendAsync(HttpMethod.Post, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await BodyAsync(response);
    }

    private static async Task<HttpResponseMessage> AddAsync(Member member, VirtualAuthenticator authenticator, string name = "Work laptop", bool userVerified = true, string? code = null)
    {
        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/options");

        return await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new
        {
            credential = authenticator.Register(options, userVerified),
            name,
            twoFactorCode = code ?? member.Factory.NextTotpCode(member.User),
        });
    }

    private static async Task<HttpResponseMessage> PasskeySignInAsync(ApiClient client, VirtualAuthenticator authenticator, bool userVerified = true, string? originOverride = null)
    {
        var options = await OptionsAsync(client, "/api/auth/passkey/sign-in/options");

        return await client.SendAsync(HttpMethod.Post, "/api/auth/passkey/sign-in", new { credential = authenticator.Assert(options, userVerified, originOverride) });
    }

    private static async Task<JsonElement> ListAsync(ApiClient client) =>
        await (await client.GetAsync("/api/auth/passkeys")).Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<int> CountAsync(ApiFactory factory) =>
        await factory.WithDbAsync(db => db.Set<IdentityUserPasskey<Guid>>().CountAsync());

    // --- Adding, listing, renaming ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_in_person_adds_a_passkey_after_a_fresh_code_and_sees_it_in_their_list()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();

        var response = await AddAsync(member, authenticator, "Work laptop");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var added = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Work laptop", added.GetProperty("name").GetString());
        Assert.Equal(authenticator.CredentialIdText, added.GetProperty("id").GetString());
        var list = await ListAsync(member.Client);
        Assert.Equal("Work laptop", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Adding_a_passkey_needs_a_valid_fresh_code_and_a_wrong_one_adds_nothing()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();

        var wrong = await AddAsync(member, authenticator, code: "000000");
        var none = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new { credential = authenticator.Register(await OptionsAsync(member.Client, "/api/auth/passkeys/options")), name = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode); // neither a code nor a passkey answer
        Assert.Equal(0, await CountAsync(member.Factory));
    }

    [Fact]
    public async Task A_passkey_that_did_not_verify_the_user_is_refused_when_adding()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();

        var response = await AddAsync(member, authenticator, userVerified: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync(member.Factory));
        Assert.Contains(await member.Factory.AuditEntriesAsync(), entry => entry.Action == "PasskeyRejected");
    }

    [Fact]
    public async Task A_passkey_made_for_another_origin_or_another_challenge_is_refused()
    {
        await using var member = await SignedInAsync();
        using var elsewhere = new VirtualAuthenticator();
        using var other = new VirtualAuthenticator();

        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/options");
        var wrongOrigin = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new { credential = elsewhere.Register(options, true, "https://evil.example.test"), name = "x", twoFactorCode = member.Factory.NextTotpCode(member.User) });
        Assert.Equal(HttpStatusCode.BadRequest, wrongOrigin.StatusCode);

        // The challenge was spent by that attempt, so an answer to it, even a good one, is no use now.
        var spent = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new { credential = other.Register(options), name = "x", twoFactorCode = member.Factory.NextTotpCode(member.User) });
        Assert.Equal(HttpStatusCode.BadRequest, spent.StatusCode);
        Assert.Equal(0, await CountAsync(member.Factory));
    }

    [Fact]
    public async Task A_person_can_rename_their_passkey_and_not_somebody_elses()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);
        var other = await member.Factory.CreateUserAsync("other@example.test");
        var otherClient = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await otherClient.SignInWithTwoFactorAsync(member.Factory, other)).StatusCode);

        var renamed = await member.Client.SendAsync(HttpMethod.Patch, $"/api/auth/passkeys/{authenticator.CredentialIdText}", new { name = "Phone" });
        var theirs = await otherClient.SendAsync(HttpMethod.Patch, $"/api/auth/passkeys/{authenticator.CredentialIdText}", new { name = "Stolen" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Phone", Assert.Single((await ListAsync(member.Client)).EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        Assert.Empty((await ListAsync(otherClient)).EnumerateArray());
    }

    [Fact]
    public async Task The_creation_options_ask_for_a_discoverable_passkey_that_verifies_the_user_for_the_configured_hostname()
    {
        await using var member = await SignedInAsync();

        using var options = JsonDocument.Parse(await OptionsAsync(member.Client, "/api/auth/passkeys/options"));
        var root = options.RootElement;

        Assert.Equal(ApiFactory.PasskeyRelyingPartyId, root.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", root.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        Assert.Equal("required", root.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("challenge").GetString()));
    }

    [Fact]
    public async Task The_ceremony_state_travels_in_a_secure_http_only_strict_cookie_never_in_the_options()
    {
        await using var member = await SignedInAsync();

        var response = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys/options");

        var cookie = ApiClient.SetCookieHeader(response, "__Host-cr-passkey");
        Assert.NotNull(cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    // --- Signing in with a passkey ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_passkey_that_verified_the_user_signs_in_as_multi_factor_with_no_password_or_code()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        var fresh = member.Factory.CreateApiClient();
        var response = await PasskeySignInAsync(fresh, authenticator);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("signed-in", await TwoFactorFlows.StatusAsync(response));
        var session = await (await fresh.GetAsync("/api/auth/session")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(session.GetProperty("twoFactorComplete").GetBoolean()); // amr = mfa
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/me")).StatusCode); // and it can reach what needs a full session
        Assert.Contains(await member.Factory.AuditEntriesAsync(), entry => entry.Action == "SignInSucceeded" && entry.Detail != null && entry.Detail.Contains("passkey", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_passkey_that_did_not_verify_the_user_cannot_sign_in()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        var fresh = member.Factory.CreateApiClient();
        var response = await PasskeySignInAsync(fresh, authenticator, userVerified: false);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task An_answer_for_another_origin_an_unknown_passkey_or_a_replay_does_not_sign_in()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        using var stranger = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeySignInAsync(member.Factory.CreateApiClient(), authenticator, originOverride: "https://evil.example.test")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeySignInAsync(member.Factory.CreateApiClient(), stranger)).StatusCode);

        // A good answer works once; the same answer again does not (its challenge is spent).
        var fresh = member.Factory.CreateApiClient();
        var options = await OptionsAsync(fresh, "/api/auth/passkey/sign-in/options");
        var answer = authenticator.Assert(options);
        Assert.Equal(HttpStatusCode.OK, (await fresh.SendAsync(HttpMethod.Post, "/api/auth/passkey/sign-in", new { credential = answer })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.SendAsync(HttpMethod.Post, "/api/auth/passkey/sign-in", new { credential = answer })).StatusCode);
    }

    [Fact]
    public async Task A_passkey_does_not_sign_in_an_account_that_is_not_approved()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);
        await member.Factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET account_status = 'Suspended'");

            return 0;
        });

        var response = await PasskeySignInAsync(member.Factory.CreateApiClient(), authenticator);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The step-up -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_passkey_answer_does_for_a_code_when_changing_the_password()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/step-up/options");
        var response = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = ApiFactory.Password,
            newPassword = "an entirely different passphrase",
            passkeyAssertion = authenticator.Assert(options),
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task A_passkey_answer_that_did_not_verify_the_user_is_not_a_step_up()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/step-up/options");
        var response = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new
        {
            currentPassword = ApiFactory.Password,
            newPassword = "an entirely different passphrase",
            passkeyAssertion = authenticator.Assert(options, userVerified: false),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Factory.CreateApiClient().SignInAsync(Email, ApiFactory.Password)).StatusCode); // the password is unchanged
    }

    [Fact]
    public async Task A_step_up_answer_cannot_be_used_twice_or_by_somebody_else()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/step-up/options");
        var answer = authenticator.Assert(options);
        Assert.Equal(HttpStatusCode.NoContent, (await member.Client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new { currentPassword = ApiFactory.Password, newPassword = "an entirely different passphrase", passkeyAssertion = answer })).StatusCode);

        var again = await member.Client.SendAsync(HttpMethod.Post, "/api/auth/change-password", new { currentPassword = "an entirely different passphrase", newPassword = "yet another different passphrase", passkeyAssertion = answer });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_passkey_answer_confirms_a_proposal_for_a_second_admin()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        await using var _ = factory;
        var admins = await factory.CreateAdminsAsync(3);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);
        var maker = factory.CreateApiClient();
        var checker = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await maker.SignInWithTwoFactorAsync(factory, admins[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await checker.SignInWithTwoFactorAsync(factory, admins[1])).StatusCode);
        var proposed = await maker.SendAsync(HttpMethod.Post, "/api/admin/pending-actions", new { type = "AssignRole", targetUserId = staff.Id, role = "CaseReviewer", reason = Proposals.Reason });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = await proposed.Content.ReadFromJsonAsync<JsonElement>();

        // The checker adds a passkey, then confirms with it instead of a code.
        using var authenticator = new VirtualAuthenticator();
        var add = await checker.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new { credential = authenticator.Register(await OptionsAsync(checker, "/api/auth/passkeys/options")), name = "Key", twoFactorCode = factory.NextTotpCode(admins[1]) });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var options = await OptionsAsync(checker, "/api/auth/passkeys/step-up/options");

        var confirm = await checker.SendAsync(
            HttpMethod.Post,
            $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm",
            new { payloadHash = proposal.GetProperty("payloadHash").GetString(), passkeyAssertion = authenticator.Assert(options) });

        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal([Role.CaseReviewer], await factory.RolesOfAsync(staff));
    }

    // --- Removing, and what stays ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_passkey_needs_a_step_up_and_removing_the_last_one_leaves_the_authenticator_working()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);
        var path = $"/api/auth/passkeys/{authenticator.CredentialIdText}/remove";

        var without = await member.Client.SendAsync(HttpMethod.Post, path, new { });
        var wrong = await member.Client.SendAsync(HttpMethod.Post, path, new { twoFactorCode = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(1, await CountAsync(member.Factory));

        var removed = await member.Client.SendAsync(HttpMethod.Post, path, new { twoFactorCode = member.Factory.NextTotpCode(member.User) });

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, await CountAsync(member.Factory));
        Assert.Empty((await ListAsync(member.Client)).EnumerateArray());

        // The password and authenticator app still sign in, and the removed passkey no longer does.
        var again = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await again.SignInWithTwoFactorAsync(member.Factory, member.User)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeySignInAsync(member.Factory.CreateApiClient(), authenticator)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_passkey_can_be_confirmed_with_another_passkey()
    {
        await using var member = await SignedInAsync();
        using var first = new VirtualAuthenticator();
        using var second = new VirtualAuthenticator();
        await AddAsync(member, first, "First");
        await AddAsync(member, second, "Second");

        var options = await OptionsAsync(member.Client, "/api/auth/passkeys/step-up/options");
        var removed = await member.Client.SendAsync(HttpMethod.Post, $"/api/auth/passkeys/{first.CredentialIdText}/remove", new { passkeyAssertion = second.Assert(options) });

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal("Second", Assert.Single((await ListAsync(member.Client)).EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Removing_a_passkey_ends_the_other_sessions()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);
        var elsewhere = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await PasskeySignInAsync(elsewhere, authenticator)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/api/me")).StatusCode);

        await member.Client.SendAsync(HttpMethod.Post, $"/api/auth/passkeys/{authenticator.CredentialIdText}/remove", new { twoFactorCode = member.Factory.NextTotpCode(member.User) });

        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.Client.GetAsync("/api/me")).StatusCode); // this session carries on
    }

    [Fact]
    public async Task A_reset_of_two_factor_removes_every_passkey_too()
    {
        await using var member = await SignedInAsync();
        using var first = new VirtualAuthenticator();
        using var second = new VirtualAuthenticator();
        await AddAsync(member, first, "First");
        await AddAsync(member, second, "Second");
        Assert.Equal(2, await CountAsync(member.Factory));

        using (var scope = member.Factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IBreakGlassService>().ResetTwoFactorAsync(member.User.Id, "lost the phone and the passkeys");
            Assert.Equal(BreakGlassOutcome.Done, result.Outcome);
        }

        Assert.Equal(0, await CountAsync(member.Factory));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PasskeySignInAsync(member.Factory.CreateApiClient(), first)).StatusCode);
    }

    // --- Notices and the audit log --------------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_and_removing_a_passkey_each_make_a_security_notice_and_an_audit_entry_that_hold_no_credential()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);
        await member.Client.SendAsync(HttpMethod.Post, $"/api/auth/passkeys/{authenticator.CredentialIdText}/remove", new { twoFactorCode = member.Factory.NextTotpCode(member.User) });

        var notices = await member.Factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(notification => notification.RecipientId == member.User.Id).Select(notification => notification.Type.ToString()).ToListAsync());
        Assert.Contains("SecurityPasskeyAdded", notices);
        Assert.Contains("SecurityPasskeyRemoved", notices);

        var entries = await member.Factory.AuditEntriesAsync();
        Assert.Contains(entries, entry => entry.Action == "PasskeyAdded" && entry.ActorUserId == member.User.Id);
        Assert.Contains(entries, entry => entry.Action == "PasskeyRemoved" && entry.ActorUserId == member.User.Id);
        var everything = string.Join('\n', entries.Select(entry => $"{entry.Detail} {entry.Subject} {entry.BeforeJson} {entry.AfterJson}"));
        Assert.DoesNotContain(authenticator.CredentialIdText, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Renaming_a_passkey_is_in_the_audit_log()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        await AddAsync(member, authenticator);

        await member.Client.SendAsync(HttpMethod.Patch, $"/api/auth/passkeys/{authenticator.CredentialIdText}", new { name = "Phone" });

        Assert.Contains(await member.Factory.AuditEntriesAsync(), entry => entry.Action == "PasskeyRenamed");
    }

    [Fact]
    public async Task An_admin_without_a_passkey_is_asked_to_add_one_each_week_and_not_once_they_have_one()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        await using var _ = factory;
        var admin = (await factory.CreateAdminsAsync(1))[0];
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, admin)).StatusCode);

        var first = await NoticesOfAsync(factory, admin, "PasskeyAdvice");
        Assert.Single(first);

        // Told again a week later while there is still none.
        factory.Clock.Advance(TimeSpan.FromDays(8));
        await EnsureTimeBasedAsync(factory, admin);
        Assert.Equal(2, (await NoticesOfAsync(factory, admin, "PasskeyAdvice")).Count);

        // (The eight days ended the first session, so they sign in again to add one.)
        var later = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await later.SignInWithTwoFactorAsync(factory, admin)).StatusCode);
        using var authenticator = new VirtualAuthenticator();
        var options = await OptionsAsync(later, "/api/auth/passkeys/options");
        Assert.Equal(HttpStatusCode.OK, (await later.SendAsync(HttpMethod.Post, "/api/auth/passkeys", new { credential = authenticator.Register(options), name = "Key", twoFactorCode = factory.NextTotpCode(admin) })).StatusCode);
        factory.Clock.Advance(TimeSpan.FromDays(8));
        await EnsureTimeBasedAsync(factory, admin);
        Assert.Equal(2, (await NoticesOfAsync(factory, admin, "PasskeyAdvice")).Count); // no third
    }

    [Fact]
    public async Task Someone_who_is_not_an_admin_is_not_asked_to_add_a_passkey()
    {
        await using var member = await SignedInAsync();

        await EnsureTimeBasedAsync(member.Factory, member.User);

        Assert.Empty(await NoticesOfAsync(member.Factory, member.User, "PasskeyAdvice"));
    }

    private static async Task EnsureTimeBasedAsync(ApiFactory factory, ApplicationUser user)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CaseRegister.Application.Notifications.INotificationService>().EnsureTimeBasedAsync(user.Id);
    }

    private static Task<List<string>> NoticesOfAsync(ApiFactory factory, ApplicationUser user, string type) =>
        factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(notification => notification.RecipientId == user.Id && notification.Type.ToString() == type).Select(notification => notification.Text).ToListAsync());

    // --- Limits ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_passkey_endpoints_carry_the_sign_in_rate_limit()
    {
        await using var member = await SignedInAsync(signInPerIpPerMinute: 3);
        var fresh = member.Factory.CreateApiClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            statuses.Add((await fresh.SendAsync(HttpMethod.Post, "/api/auth/passkey/sign-in/options")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, statuses[0]);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Every_passkey_endpoint_needs_an_anti_forgery_token_and_the_list_needs_a_full_session()
    {
        await using var member = await SignedInAsync();

        foreach (var path in new[] { "/api/auth/passkeys/options", "/api/auth/passkeys/step-up/options", "/api/auth/passkey/sign-in/options" })
        {
            var response = await member.Client.SendAsync(HttpMethod.Post, path, withCsrfToken: false);
            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden, $"{path} answered {response.StatusCode} without a token");
        }

        var anonymous = member.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/passkeys")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/auth/passkeys/options")).StatusCode);
    }

    [Fact]
    public async Task A_person_can_have_ten_passkeys_and_no_more()
    {
        await using var member = await SignedInAsync();
        var authenticators = new List<VirtualAuthenticator>();
        try
        {
            for (var index = 0; index < PasskeyService.MaxPerPerson; index++)
            {
                var authenticator = new VirtualAuthenticator();
                authenticators.Add(authenticator);
                Assert.Equal(HttpStatusCode.OK, (await AddAsync(member, authenticator, $"Key {index}")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.Conflict, (await member.Client.SendAsync(HttpMethod.Post, "/api/auth/passkeys/options")).StatusCode);
        }
        finally
        {
            authenticators.ForEach(authenticator => authenticator.Dispose());
        }
    }

    [Fact]
    public async Task The_same_passkey_cannot_be_added_twice()
    {
        await using var member = await SignedInAsync();
        using var authenticator = new VirtualAuthenticator();
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(member, authenticator)).StatusCode);

        var again = await AddAsync(member, authenticator, "Again");

        Assert.NotEqual(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await CountAsync(member.Factory));
    }

    // --- The settings -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", false, "RelyingPartyId is not configured")]
    [InlineData("App.Example.Test", false, "bare lower-case hostname")]
    [InlineData("https://app.example.test", false, "bare lower-case hostname")]
    [InlineData("app.example.test:443", false, "bare lower-case hostname")]
    [InlineData("localhost", true, "not 'localhost'")]
    [InlineData("caseregister-api.onrender.com", true, "not 'caseregister-api.onrender.com'")]
    public void The_relying_party_id_has_to_be_the_hostname_the_browser_sees(string id, bool production, string expected)
    {
        var problem = new PasskeyOptions { RelyingPartyId = id }.Problem(production);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("caseregister.pages.dev", true)]
    [InlineData("localhost", false)]
    public void A_proper_hostname_is_accepted(string id, bool production) =>
        Assert.Null(new PasskeyOptions { RelyingPartyId = id }.Problem(production));

    [Theory]
    [InlineData("http://app.example.test", false)] // not https
    [InlineData("https://evil.example.net", false)] // not the hostname or a subdomain of it
    [InlineData("https://app.example.test/path", false)]
    [InlineData("http://localhost:5173", true)] // localhost in production
    public void The_origins_have_to_be_https_origins_of_the_hostname(string origin, bool production)
    {
        var options = new PasskeyOptions { RelyingPartyId = production ? "app.example.test" : "app.example.test", Origins = [origin] };

        Assert.NotNull(options.Problem(production));
    }

    [Fact]
    public void Without_origins_the_hostname_s_own_https_origin_is_the_one_accepted()
    {
        Assert.Equal(["https://app.example.test"], new PasskeyOptions { RelyingPartyId = "app.example.test" }.EffectiveOrigins);
    }

    [Fact]
    public void Production_will_not_start_with_the_render_hostname_as_the_relying_party()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Passkeys:RelyingPartyId"] = "caseregister-api.onrender.com", environmentName: "Production");

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Passkeys:RelyingPartyId", failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFactory.ProxyTestSecret, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_step_up_has_to_be_exactly_one_of_a_code_or_a_passkey_answer()
    {
        Assert.True(new StepUp("123456", null).IsValidShape);
        Assert.True(new StepUp(null, "{}").IsValidShape);
        Assert.False(new StepUp(null, null).IsValidShape);
        Assert.False(new StepUp("", "  ").IsValidShape);
        Assert.False(new StepUp("123456", "{}").IsValidShape);
    }

    [Theory]
    [InlineData("  Work laptop  ", "Work laptop")]
    [InlineData("", "Passkey")]
    [InlineData("a\u0007b", "ab")]
    public void A_name_is_trimmed_cleaned_and_never_empty(string given, string expected) =>
        Assert.Equal(expected, PasskeyService.CleanName(given));

    [Fact]
    public void A_name_is_cut_to_sixty_characters() =>
        Assert.Equal(60, PasskeyService.CleanName(new string('x', 200)).Length);
}
