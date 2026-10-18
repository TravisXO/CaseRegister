using System.Net;
using CaseRegister.Api.Cli;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>Lost phone: the reset of somebody's two-factor, the one-time enrolment code that follows, and the break-glass command for when two admins cannot do it.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class EnrolmentCodeTests(PostgresFixture postgres)
{
    private const string Identity = "Checked their ID card in person at the office and phoned the number we hold.";

    private sealed record World(ApiFactory Factory, ApplicationUser A, ApplicationUser B, ApplicationUser C, Agency Agency, ApplicationUser Person) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        /// <summary>Proposes the reset (A) and confirms it (B), returning the one-time code the confirming admin is shown.</summary>
        public async Task<string> ResetAsync(ApplicationUser? target = null, ApplicationUser? maker = null, ApplicationUser? checker = null)
        {
            var proposal = await Factory.ProposeOkAsync(maker ?? A, Command(target ?? Person));
            var confirmed = await Factory.ConfirmWithLinkAsync(checker ?? B, proposal);

            return confirmed.EnrolmentCode!;
        }

        public Task<List<EnrolmentCodeRecord>> Codes() =>
            Factory.WithDbAsync(db => db.EnrolmentCodes.AsNoTracking().OrderBy(code => code.CreatedAt).ToListAsync());
    }

    private static ProposeActionCommand Command(ApplicationUser target, string reason = Identity) =>
        new(PendingActionType.ResetTwoFactor, target.Id, null, null, null, reason);

    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var admins = await factory.CreateAdminsAsync(3);
        var person = await factory.CreateMemberAsync("person@example.test", agency, [Role.RegisteredAgent]);

        return new World(factory, admins[0], admins[1], admins[2], agency, person);
    }

    private static Task<HttpResponseMessage> RedeemAsync(ApiClient client, string code) =>
        client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrolment-code", new { code });

    // --- Who may reset ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Neither_the_proposer_nor_the_confirmer_can_be_the_person_whose_two_factor_it_is()
    {
        await using var world = await WorldAsync();
        // A cannot propose a reset of A's own two-factor.
        var self = await world.Factory.ProposeAsync(world.A, Command(world.A));
        Assert.Equal(ProposeOutcome.Refused, self.Outcome);

        // C proposes a reset of B's two-factor; B cannot confirm it.
        var proposal = await world.Factory.ProposeOkAsync(world.C, Command(world.B));
        var byTarget = await world.Factory.ConfirmAsync(world.B, proposal);
        Assert.Equal(ConfirmOutcome.NotAllowed, byTarget.Outcome);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.B.Id)));
    }

    [Fact]
    public async Task With_only_two_admins_an_admins_two_factor_cannot_be_reset_by_the_normal_process()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        // A proposes a reset of B. The only other admin able to confirm is B, who is the target.
        var proposal = await factory.ProposeOkAsync(admins[0], Command(admins[1]));
        Assert.Equal(ConfirmOutcome.NotAllowed, (await factory.ConfirmAsync(admins[1], proposal)).Outcome);
        Assert.Equal(ConfirmOutcome.NotAllowed, (await factory.ConfirmAsync(admins[0], proposal)).Outcome); // and the proposer cannot confirm their own

        Assert.Equal(1, await factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == admins[1].Id)));
    }

    [Fact]
    public async Task The_reason_has_to_say_how_the_identity_was_checked()
    {
        var validator = new ProposeActionCommandValidator();
        var target = Guid.NewGuid();

        Assert.False(validator.Validate(new ProposeActionCommand(PendingActionType.ResetTwoFactor, target, null, null, null, "Phoned them, fine.")).IsValid);
        Assert.True(validator.Validate(new ProposeActionCommand(PendingActionType.ResetTwoFactor, target, null, null, null, Identity)).IsValid);

        // Other actions keep the shorter minimum.
        Assert.True(validator.Validate(new ProposeActionCommand(PendingActionType.SuspendUser, target, null, null, null, "Complaints, under review.")).IsValid);
    }

    // --- The code ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_a_reset_shows_the_code_once_and_keeps_only_its_hash_and_a_72_hour_expiry()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Command(world.Person));

        var first = await world.Factory.ConfirmWithLinkAsync(world.B, proposal);

        Assert.NotNull(first.EnrolmentCode);
        Assert.True(EnrolmentCodes.LooksLikeEnrolmentCode(first.EnrolmentCode));
        var stored = Assert.Single(await world.Codes());
        Assert.NotEqual(first.EnrolmentCode, stored.CodeHash);
        Assert.Equal(EnrolmentCodes.Hash(first.EnrolmentCode), stored.CodeHash);
        Assert.Equal(world.Factory.Clock.GetUtcNow() + TimeSpan.FromHours(72), stored.ExpiresAt, TimeSpan.FromSeconds(5));
        Assert.Null(stored.UsedAt);

        // Not in the proposal as it is read back, and not in the audit log.
        var again = await world.Factory.GetAsync(proposal.Id);
        Assert.DoesNotContain(first.EnrolmentCode, again.Payload + again.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(
            await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Detail + "|" + e.Subject + "|" + e.AfterJson).ToListAsync()),
            text => text.Contains(EnrolmentCodes.Normalise(first.EnrolmentCode), StringComparison.OrdinalIgnoreCase) || text.Contains(first.EnrolmentCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reset_rotates_the_security_stamp_and_ends_every_session()
    {
        await using var world = await WorldAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.Person);
        var before = (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp;

        await world.ResetAsync();

        Assert.NotEqual(before, (await world.Factory.GetUserAsync(world.Person.Email!)).SecurityStamp);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Sessions.CountAsync(session => session.UserId == world.Person.Id)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task After_a_reset_the_password_alone_leads_to_the_code_and_nothing_else_can_be_reached()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();

        var client = world.Factory.CreateApiClient();
        var signIn = await client.SignInAsync(world.Person.Email!);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal("enrolment-code-required", await TwoFactorFlows.StatusAsync(signIn));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode); // no session at all yet
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        // A wrong code, and a right-shaped guess, open nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(client, "AAAA-BBBB-CCCC")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(client, "12")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode);

        // The right code opens a session that can enrol and nothing more.
        var redeemed = await RedeemAsync(client, code);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(redeemed));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/me")).StatusCode);

        // They enrol again, and are back to normal: the next sign-in asks for the authenticator's code, not the enrolment code.
        await TwoFactorFlows.EnrolFullyAsync(world.Factory, client, world.Person);
        Assert.Equal(HttpStatusCode.OK, (await world.Factory.CreateApiClient().SignInWithTwoFactorAsync(world.Factory, world.Person)).StatusCode);
        Assert.Equal(2, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "EnrolmentCodeUsed" || e.Action == "TwoFactorEnrolled")));
    }

    [Fact]
    public async Task The_code_is_typed_however_it_is_written_down()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    public async Task The_code_is_not_used_up_by_typing_it_in_so_abandoning_set_up_and_coming_back_still_works()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();

        // Redeem it, start setting up, and walk away.
        var first = world.Factory.CreateApiClient();
        await first.SignInAsync(world.Person.Email!);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(first, code)).StatusCode);
        var (start, _, _) = await TwoFactorFlows.StartEnrolmentAsync(first);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Null((await world.Codes()).Single().UsedAt);

        // Later, a fresh sign-in is still asked for the code, and the same code still works.
        world.Factory.Clock.Advance(TimeSpan.FromHours(30));
        var second = world.Factory.CreateApiClient();
        Assert.Equal("enrolment-code-required", await TwoFactorFlows.StatusAsync(await second.SignInAsync(world.Person.Email!)));
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(second, code)).StatusCode);
        await TwoFactorFlows.EnrolFullyAsync(world.Factory, second, world.Person);

        // Enrolment is finished: the code is used up for good.
        var used = Assert.Single(await world.Codes());
        Assert.NotNull(used.UsedAt);
        var after = world.Factory.CreateApiClient();
        Assert.Equal("two-factor-required", await TwoFactorFlows.StatusAsync(await after.SignInAsync(world.Person.Email!)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(after, code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(world.Factory.CreateApiClient(), code)).StatusCode); // and no step cookie at all
    }

    [Fact]
    public async Task Each_wrong_try_counts_towards_the_second_factor_limit_and_ten_in_a_row_lock_it()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);

        for (var wrong = 0; wrong < 10; wrong++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(client, "AAAA-BBBB-CCCC")).StatusCode);
        }

        Assert.Equal(10, await world.Factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.UserId == world.Person.Id && f.Kind == CaseRegister.Infrastructure.Persistence.FailureKind.SecondFactor && f.ClearedAt == null)));

        // Locked: even the right code is not accepted now.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(client, code)).StatusCode);
    }

    [Fact]
    public async Task A_right_code_ends_the_run_of_wrong_ones()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);

        for (var wrong = 0; wrong < 4; wrong++)
        {
            await RedeemAsync(client, "AAAA-BBBB-CCCC");
        }

        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, code)).StatusCode);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.SignInFailures.CountAsync(f => f.UserId == world.Person.Id && f.ClearedAt == null)));
    }

    [Fact]
    public async Task The_code_expires_after_72_hours()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();

        world.Factory.Clock.Advance(TimeSpan.FromHours(71));
        var inTime = world.Factory.CreateApiClient();
        await inTime.SignInAsync(world.Person.Email!);
        world.Factory.Clock.Advance(TimeSpan.FromHours(2));
        var late = world.Factory.CreateApiClient();
        await late.SignInAsync(world.Person.Email!);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(late, code)).StatusCode);
        Assert.Null((await world.Codes()).Single().UsedAt);

        // An expired code does not open the account up to a plain password: it needs a new reset.
        Assert.Equal("enrolment-code-required", await TwoFactorFlows.StatusAsync(await late.SignInAsync(world.Person.Email!)));
    }

    [Fact]
    public async Task A_new_reset_replaces_a_code_that_was_never_used_and_the_old_one_stops_working()
    {
        await using var world = await WorldAsync();
        var old = await world.ResetAsync();

        var fresh = await world.ResetAsync(maker: world.B, checker: world.C); // the account has no authenticator now, but is waiting for one
        Assert.NotEqual(old, fresh);

        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(client, old)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(client, fresh)).StatusCode);
        Assert.Equal(1, (await world.Codes()).Count(code => code.RevokedAt != null));
    }

    [Fact]
    public async Task A_session_that_has_not_been_through_the_code_cannot_start_enrolling()
    {
        await using var world = await WorldAsync();
        var bare = await world.Factory.CreateUserAsync("bare@example.test", twoFactor: false);
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(world.Factory, bare);

        // A code outstanding for the account (say, created while this session was open) closes the way in.
        await world.Factory.WithDbAsync(async db =>
        {
            db.EnrolmentCodes.Add(new EnrolmentCodeRecord(bare.Id, EnrolmentCodes.Hash(EnrolmentCodes.Generate()), world.Factory.Clock.GetUtcNow().AddHours(72)));
            await db.SaveChangesAsync();

            return 0;
        });

        Assert.Equal(HttpStatusCode.Conflict, (await restricted.SendAsync(HttpMethod.Post, "/api/auth/2fa/enrol")).StatusCode);
    }

    [Fact]
    public async Task Every_wrong_try_is_audited_and_the_code_is_never_in_a_log()
    {
        await using var world = await WorldAsync();
        var code = await world.ResetAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync(world.Person.Email!);

        await RedeemAsync(client, "AAAA-BBBB-CCCC");
        await RedeemAsync(client, code);

        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "TwoFactorFailed" && e.Detail == "invalid enrolment code")));
        Assert.DoesNotContain(world.Factory.Logs.Entries, entry => entry.Message.Contains(code, StringComparison.OrdinalIgnoreCase));
    }

    // --- Break-glass ----------------------------------------------------------------------------------------------

    private static IConfiguration ConfigurationFor(ApiFactory factory) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"],
            ["ConnectionStrings:Migrations"] = factory.OwnerConnectionString,
        }).Build();

    private static Task<int> RunBreakGlassAsync(ApiFactory factory, ScriptedTerminal terminal, string command = BreakGlassCommands.ResetTwoFactorName, string[]? args = null) =>
        BreakGlassCommands.RunAsync(args ?? [command], factory.Services, ConfigurationFor(factory), terminal);

    private static ScriptedTerminal Answers(string email, string reason = Identity, string? again = null) => new([email, reason, again ?? email], []);

    [Fact]
    public async Task The_command_resets_two_factor_audits_it_and_tells_every_admin_and_the_person()
    {
        await using var world = await WorldAsync();
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.Person);
        var terminal = Answers(world.Person.Email!);

        var exitCode = await RunBreakGlassAsync(world.Factory, terminal);

        Assert.Equal(BreakGlassCommands.Succeeded, exitCode);
        var codeLine = Assert.Single(terminal.Output, line => line.StartsWith("One-time enrolment code", StringComparison.Ordinal));
        var code = codeLine[(codeLine.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
        Assert.True(EnrolmentCodes.LooksLikeEnrolmentCode(code));
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.Person.Id)));
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Sessions.CountAsync(session => session.UserId == world.Person.Id)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);

        // Audited, with the reason, and with no actor (it was the command line); the code itself is not in the log.
        var entry = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().SingleAsync(e => e.Action == "BreakGlassUsed"));
        Assert.Equal(world.Person.Id.ToString(), entry.Subject);
        Assert.Null(entry.ActorUserId);
        Assert.Contains(Identity, entry.Detail, StringComparison.Ordinal);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "TwoFactorReset")));
        Assert.DoesNotContain(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Detail + "|" + e.AfterJson).ToListAsync()), text => text.Contains(code, StringComparison.Ordinal));

        // Every admin and the person get a critical notification, once each; nobody else does.
        foreach (var recipient in new[] { world.A, world.B, world.C, world.Person })
        {
            var mine = await world.Factory.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.RecipientId == recipient.Id && n.Type == NotificationType.BreakGlass).ToListAsync());
            Assert.Equal(NotificationSeverity.Critical, Assert.Single(mine).Severity);
        }

        Assert.Equal(4, await world.Factory.WithDbAsync(db => db.Notifications.CountAsync(n => n.Type == NotificationType.BreakGlass)));

        // And the code works.
        var next = world.Factory.CreateApiClient();
        await next.SignInAsync(world.Person.Email!);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(next, code)).StatusCode);
    }

    [Fact]
    public async Task It_works_on_an_admin_even_when_there_are_only_two()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        Assert.Equal(BreakGlassCommands.Succeeded, await RunBreakGlassAsync(factory, Answers(admins[1].Email!)));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == admins[1].Id)));
    }

    [Fact]
    public async Task It_shows_the_database_host_asks_for_the_email_and_a_reason_and_asks_again_before_it_acts()
    {
        await using var world = await WorldAsync();
        var terminal = Answers(world.Person.Email!);

        await RunBreakGlassAsync(world.Factory, terminal);

        Assert.Contains(terminal.Output, line => line.StartsWith("This will change accounts on database host '", StringComparison.Ordinal));
        Assert.Equal(["Email of the person: ", "Reason (how their identity was checked, and why): ", "Type the person's email again to confirm: "], terminal.LinePrompts);
    }

    [Theory]
    [InlineData("nobody@example.test", Identity, null)]
    [InlineData("person@example.test", "short", null)]
    [InlineData("person@example.test", Identity, "other@example.test")]
    public async Task It_refuses_and_changes_nothing_when_the_details_are_wrong(string email, string reason, string? again)
    {
        await using var world = await WorldAsync();

        var exitCode = await RunBreakGlassAsync(world.Factory, Answers(email, reason, again));

        Assert.Equal(BreakGlassCommands.Refused, exitCode);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.Person.Id)));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "BreakGlassRefused")));
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "BreakGlassUsed")));
    }

    [Fact]
    public async Task It_refuses_when_it_is_not_interactive_or_is_given_arguments()
    {
        await using var world = await WorldAsync();

        Assert.Equal(BreakGlassCommands.Refused, await RunBreakGlassAsync(world.Factory, new ScriptedTerminal([world.Person.Email!, Identity, world.Person.Email!], [], interactive: false)));
        Assert.Equal(BreakGlassCommands.Refused, await RunBreakGlassAsync(world.Factory, Answers(world.Person.Email!), args: [BreakGlassCommands.ResetTwoFactorName, world.Person.Email!]));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.Person.Id)));
    }

    [Fact]
    public async Task It_allows_one_break_glass_action_per_person_per_24_hours()
    {
        await using var world = await WorldAsync();
        Assert.Equal(BreakGlassCommands.Succeeded, await RunBreakGlassAsync(world.Factory, Answers(world.Person.Email!)));

        var second = Answers(world.Person.Email!);
        Assert.Equal(BreakGlassCommands.Refused, await RunBreakGlassAsync(world.Factory, second));
        Assert.Contains(second.Output, line => line.Contains("last 24 hours", StringComparison.Ordinal));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "BreakGlassUsed")));

        // Another person is not held up by it, and after 24 hours the same person can be done again.
        Assert.Equal(BreakGlassCommands.Succeeded, await RunBreakGlassAsync(world.Factory, Answers(world.B.Email!)));
        world.Factory.Clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(BreakGlassCommands.Succeeded, await RunBreakGlassAsync(world.Factory, Answers(world.Person.Email!)));
    }

    [Fact]
    public async Task It_refuses_to_run_when_its_audit_key_differs_from_the_latest_entrys()
    {
        await using var world = await WorldAsync();
        await world.Factory.WriteManyAsync(1);

        await using var rotated = world.Factory.CreateSibling(settings =>
        {
            settings["Audit:CurrentKeyId"] = ApiFactory.SecondKeyId;
            settings[$"Audit:Keys:{ApiFactory.SecondKeyId}"] = ApiFactory.SecondKeyBase64;
        });
        var terminal = Answers(world.Person.Email!);

        var exitCode = await BreakGlassCommands.RunAsync([BreakGlassCommands.ResetTwoFactorName], rotated.Services, ConfigurationFor(world.Factory), terminal);

        Assert.Equal(BreakGlassCommands.Refused, exitCode);
        Assert.Contains(terminal.Output, line => line.Contains("signed with key 'k1'", StringComparison.Ordinal));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.Person.Id)));
        Assert.Empty(terminal.LinePrompts); // it did not even get as far as asking
    }
}
