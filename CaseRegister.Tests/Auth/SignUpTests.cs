using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>An agent applying to join: what it creates, what it hides, and what it refuses.</summary>
[Collection(PostgresTestGroup.Name)]
public class SignUpTests(PostgresFixture postgres)
{
    private const string Email = "jane@example.test";

    private static Task<ApplicationUser> ApplicantAsync(ApiFactory factory, string email = Email) => factory.GetUserAsync(email);

    private static Task<MemberApplication> ApplicationOfAsync(ApiFactory factory, ApplicationUser user) =>
        factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().SingleAsync(application => application.UserId == user.Id));

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

    // --- What a valid sign-up creates ---------------------------------------------------------------------

    [Fact]
    public async Task A_valid_sign_up_creates_a_pending_account_and_an_application_and_nothing_more()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory, membershipNumber: "AGT 884", certificateNumber: $"9611/{factory.Today.Year}", agencyName: "  Copperbelt Homes Ltd "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("If your details are valid, your application has been submitted.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());

        var user = await ApplicantAsync(factory);
        Assert.Equal(AccountStatus.Pending, user.AccountStatus);
        Assert.Equal("Jane Mwansa", user.FullName);
        Assert.NotNull(user.PasswordHash);
        Assert.Null(user.MembershipNumber); // a claim, not a fact, until it is approved
        Assert.Null(user.MembershipClass);
        Assert.Null(user.AgencyId);
        Assert.Empty(await factory.RolesOfAsync(user));
        Assert.Equal(0, await factory.WithDbAsync(db => db.PractisingCertificates.CountAsync())); // the certificate is unverified: it is only a claim
        Assert.Equal(0, await factory.WithDbAsync(db => db.Agencies.CountAsync())); // and no agency is made, or matched, by itself

        var application = await ApplicationOfAsync(factory, user);
        Assert.Equal(ApplicationStatus.Pending, application.Status);
        Assert.Equal(884, application.MembershipNumber.Value);
        Assert.Equal(MembershipClass.Full, application.MembershipClass);
        Assert.Equal("Copperbelt Homes Ltd", application.AgencyName);
        Assert.Equal($"9611/{factory.Today.Year}", application.CertificateNumber.ToString());
        Assert.Equal(ApplicationConcerns.None, application.Flags);
        Assert.True((factory.Clock.GetUtcNow() - application.SubmittedAt).Duration() < TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task The_applicant_cannot_sign_in_and_gets_no_session()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var client = factory.CreateApiClient();

        await SignUps.PostAsync(client, SignUps.Body(factory));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateApiClient().SignInAsync(Email, SignUps.StrongPassword)).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Sessions.CountAsync()));
    }

    [Fact]
    public async Task The_membership_class_and_number_forms_are_read_like_everywhere_else()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        foreach (var (email, typed, expected) in new[] { ("a@example.test", "agt-77", 77), ("b@example.test", "AGT78", 78), ("c@example.test", " 79 ", 79) })
        {
            var user = await SignUps.ApplyAsync(factory, email, SignUps.Body(factory, email, membershipNumber: typed, membershipClass: "Associate"));

            var application = await ApplicationOfAsync(factory, user);
            Assert.Equal(expected, application.MembershipNumber.Value);
            Assert.Equal(MembershipClass.Associate, application.MembershipClass);
        }
    }

    // --- Mistakes are shown; whether an account exists is not ------------------------------------------------

    [Theory]
    [InlineData("Email", "not-an-email")]
    [InlineData("Email", "")]
    [InlineData("FullName", "")]
    [InlineData("AgencyName", " ")]
    [InlineData("MembershipNumber", "ABC")]
    [InlineData("MembershipNumber", "0")]
    [InlineData("MembershipNumber", "1000000")]
    [InlineData("CertificateNumber", "9611-2026")]
    [InlineData("CertificateNumber", "1234567/2026")]
    [InlineData("CertificateNumber", "")]
    [InlineData("Password", "short")]
    [InlineData("Password", "")]
    public async Task Mistakes_in_the_details_are_listed_and_nothing_is_created(string field, string value)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var year = factory.Today.Year;
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = "Jane Mwansa",
            ["email"] = Email,
            ["password"] = SignUps.StrongPassword,
            ["membershipNumber"] = "AGT 884",
            ["membershipClass"] = "Full",
            ["agencyName"] = "Copperbelt Homes",
            ["certificateNumber"] = $"9611/{year}",
            ["certificateYear"] = year,
            ["turnstileToken"] = FakeTurnstileVerifier.ValidToken,
        };
        body[char.ToLowerInvariant(field[0]) + field[1..]] = value;

        var response = await SignUps.PostAsync(factory.CreateApiClient(), body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ErrorsAsync(response)).TryGetProperty(field, out _), $"no error for {field}");
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task The_password_rules_of_every_account_apply_including_breaches_and_the_applicants_own_details()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.BreachedPasswords.Add("password1234567");
        var client = factory.CreateApiClient();

        var breached = await SignUps.PostAsync(client, SignUps.Body(factory, password: "password1234567"));
        var personal = await SignUps.PostAsync(client, SignUps.Body(factory, password: "jane@example.test jane"));
        var short12 = await SignUps.PostAsync(client, SignUps.Body(factory, password: "elevenchars"));

        foreach (var response in new[] { breached, personal, short12 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await ErrorsAsync(response)).TryGetProperty("Password", out _));
        }

        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task A_breach_check_that_cannot_be_completed_refuses_the_password_rather_than_accepting_it_unchecked()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.Unavailable = true;

        var response = await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task A_mistake_is_shown_the_same_way_whether_or_not_the_email_already_has_an_account()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("taken@example.test");
        var client = factory.CreateApiClient();

        var fresh = await SignUps.PostAsync(client, SignUps.Body(factory, "free@example.test", password: "short"));
        var existing = await SignUps.PostAsync(client, SignUps.Body(factory, "taken@example.test", password: "short"));

        Assert.Equal(HttpStatusCode.BadRequest, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, existing.StatusCode);
        Assert.Equal((await ErrorsAsync(fresh)).GetRawText(), (await ErrorsAsync(existing)).GetRawText());
    }

    // --- The certificate year -------------------------------------------------------------------------------

    // The clock starts at the real time and can only move forward, so these use next year as "the year" and move to dates in it.
    private static int NextYear(ApiFactory factory) => factory.Clock.GetUtcNow().Year + 1;

    [Fact]
    public async Task The_certificate_must_be_for_the_current_year_and_for_the_year_in_its_number()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var year = NextYear(factory);
        factory.Clock.SetUtcNow(new DateTimeOffset(year, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var client = factory.CreateApiClient();

        var current = await SignUps.PostAsync(client, SignUps.Body(factory, "a@example.test", year: year));
        var last = await SignUps.PostAsync(client, SignUps.Body(factory, "b@example.test", year: year - 1));
        var next = await SignUps.PostAsync(client, SignUps.Body(factory, "c@example.test", year: year + 1));
        var mismatch = await SignUps.PostAsync(client, SignUps.Body(factory, "d@example.test", certificateNumber: $"1/{year}", year: year + 1));

        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, last.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, next.StatusCode); // not before December
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.True((await ErrorsAsync(mismatch)).TryGetProperty("CertificateYear", out _));
    }

    [Fact]
    public async Task In_December_next_years_certificate_is_accepted_too_from_midnight_in_Zambia()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var year = NextYear(factory);
        var client = factory.CreateApiClient();

        // 30 November 23:59:59 in Zambia (UTC+2) is 21:59:59 UTC: still November.
        factory.Clock.SetUtcNow(new DateTimeOffset(year, 11, 30, 21, 59, 59, TimeSpan.Zero));
        var november = await SignUps.PostAsync(client, SignUps.Body(factory, "a@example.test", year: year + 1));

        // One second later it is 1 December in Zambia.
        factory.Clock.SetUtcNow(new DateTimeOffset(year, 11, 30, 22, 0, 0, TimeSpan.Zero));
        var december = await SignUps.PostAsync(client, SignUps.Body(factory, "b@example.test", year: year + 1));
        var decemberCurrent = await SignUps.PostAsync(client, SignUps.Body(factory, "c@example.test", year: year));
        var yearAfterNext = await SignUps.PostAsync(client, SignUps.Body(factory, "d@example.test", year: year + 2));

        Assert.Equal(HttpStatusCode.BadRequest, november.StatusCode);
        Assert.Equal(HttpStatusCode.OK, december.StatusCode);
        Assert.Equal(HttpStatusCode.OK, decemberCurrent.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, yearAfterNext.StatusCode);
    }

    [Fact]
    public async Task On_the_first_of_January_last_years_certificate_is_no_longer_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var year = NextYear(factory);
        var client = factory.CreateApiClient();

        factory.Clock.SetUtcNow(new DateTimeOffset(year, 12, 31, 21, 59, 59, TimeSpan.Zero)); // still 31 December in Zambia
        var before = await SignUps.PostAsync(client, SignUps.Body(factory, "a@example.test", year: year));
        factory.Clock.SetUtcNow(new DateTimeOffset(year, 12, 31, 22, 0, 0, TimeSpan.Zero)); // 1 January the year after
        var lastYear = await SignUps.PostAsync(client, SignUps.Body(factory, "b@example.test", year: year));
        var thisYear = await SignUps.PostAsync(client, SignUps.Body(factory, "c@example.test", year: year + 1));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, lastYear.StatusCode);
        Assert.Equal(HttpStatusCode.OK, thisYear.StatusCode);
    }


    // --- An account that already exists is never revealed ---------------------------------------------------------

    private async Task<(ApiFactory Factory, Dictionary<string, string> Emails)> FactoryWithEveryKindOfAccountAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);

        // Pending: applied once. Approved and Suspended: made directly. Rejected: applied, then rejected by two admins.
        await SignUps.ApplyAsync(factory, "pending@example.test");
        await factory.CreateUserAsync("approved@example.test");
        await factory.CreateUserAsync("suspended@example.test", AccountStatus.Suspended);
        var rejected = await SignUps.ApplyAsync(factory, "rejected@example.test");
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RejectApplication(rejected));

        return (factory, new Dictionary<string, string>
        {
            ["a new address"] = "new@example.test",
            ["a pending account"] = "pending@example.test",
            ["an approved account"] = "approved@example.test",
            ["a suspended account"] = "suspended@example.test",
            ["a rejected account"] = "rejected@example.test",
            ["an admin"] = "admin1@example.test",
            ["the same address in other capitals"] = "PENDING@Example.Test",
            ["the same address with spaces around"] = "  approved@example.test ",
        });
    }

    [Fact]
    public async Task Every_valid_sign_up_gets_exactly_the_same_answer_whatever_exists()
    {
        var (factory, emails) = await FactoryWithEveryKindOfAccountAsync();
        await using var _ = factory;
        var client = factory.CreateApiClient();
        var answers = new Dictionary<string, (HttpStatusCode Status, string Body)>();

        foreach (var (description, email) in emails)
        {
            var response = await SignUps.PostAsync(client, SignUps.Body(factory, email));
            answers[description] = (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var reference = answers["a new address"];
        Assert.Equal(HttpStatusCode.OK, reference.Status);
        Assert.All(answers, answer => Assert.Equal(reference, answer.Value));
    }

    [Fact]
    public async Task Every_path_does_the_same_slow_work_one_password_hash_and_one_breach_check()
    {
        var (factory, emails) = await FactoryWithEveryKindOfAccountAsync();
        await using var _ = factory;
        var client = factory.CreateApiClient();

        foreach (var (description, email) in emails)
        {
            var hashesBefore = factory.PasswordVerifications.Hashes;
            var checksBefore = factory.Breaches.CheckedPasswords.Count;

            await SignUps.PostAsync(client, SignUps.Body(factory, email));

            Assert.True(factory.PasswordVerifications.Hashes - hashesBefore == 1, $"{description}: {factory.PasswordVerifications.Hashes - hashesBefore} password hashes");
            Assert.True(factory.Breaches.CheckedPasswords.Count - checksBefore == 1, $"{description}: {factory.Breaches.CheckedPasswords.Count - checksBefore} breach checks");
        }
    }

    [Fact]
    public async Task An_email_that_is_pending_approved_or_suspended_changes_nothing()
    {
        var (factory, _) = await FactoryWithEveryKindOfAccountAsync();
        await using var _ = factory;
        var client = factory.CreateApiClient();

        foreach (var email in new[] { "pending@example.test", "approved@example.test", "suspended@example.test", "admin1@example.test" })
        {
            var before = await factory.GetUserAsync(email);
            var applicationsBefore = await factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().OrderBy(a => a.UserId).ToListAsync());
            var usersBefore = await factory.WithDbAsync(db => db.Users.CountAsync());

            await SignUps.PostAsync(client, SignUps.Body(factory, email, fullName: "Someone Else", password: "a completely different password", agencyName: "Other Agency", membershipNumber: "AGT 31337"));

            var after = await factory.GetUserAsync(email);
            Assert.Equal(before.PasswordHash, after.PasswordHash);
            Assert.Equal(before.FullName, after.FullName);
            Assert.Equal(before.AccountStatus, after.AccountStatus);
            Assert.Equal(before.SecurityStamp, after.SecurityStamp);
            Assert.Equal(before.ConcurrencyStamp, after.ConcurrencyStamp); // not touched at all
            Assert.Equal(usersBefore, await factory.WithDbAsync(db => db.Users.CountAsync()));
            Assert.Equal(applicationsBefore.Count, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
            Assert.Equal(applicationsBefore.Select(a => (a.Id, a.AgencyName, a.Version)), (await factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().OrderBy(a => a.UserId).ToListAsync())).Select(a => (a.Id, a.AgencyName, a.Version)));
        }
    }

    [Fact]
    public async Task An_ignored_sign_up_is_logged_without_saying_whose_account_it_was_in_the_clear()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("approved@example.test");

        await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory, "approved@example.test"));

        var entry = Assert.Single(await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "MemberSignUpIgnored").ToListAsync()));
        Assert.StartsWith("email-hmac:p1:", entry.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain("approved@example.test", entry.Subject + entry.Detail, StringComparison.Ordinal);
        Assert.Contains("Approved", entry.Detail, StringComparison.Ordinal);
    }

    // --- Reapplying after a rejection ------------------------------------------------------------------------

    [Fact]
    public async Task A_rejected_applicant_who_applies_again_goes_back_to_pending_with_the_new_details_and_a_flag()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var first = await SignUps.ApplyAsync(factory, Email, SignUps.Body(factory, Email, fullName: "Jane Mwansa", agencyName: "Old Agency"));
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RejectApplication(first, "The certificate number is not on our register."));
        var rejected = await ApplicantAsync(factory);
        Assert.Equal(AccountStatus.Rejected, rejected.AccountStatus);

        var response = await SignUps.PostAsync(
            factory.CreateApiClient(),
            SignUps.Body(factory, Email, fullName: "Jane K. Mwansa", password: "a different long password", agencyName: "New Agency", membershipNumber: "AGT 777", membershipClass: "Associate"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var again = await ApplicantAsync(factory);
        Assert.Equal(first.Id, again.Id); // the same account, not a second one
        Assert.Equal(AccountStatus.Pending, again.AccountStatus);
        Assert.Equal("Jane K. Mwansa", again.FullName);
        Assert.NotEqual(rejected.PasswordHash, again.PasswordHash);
        Assert.NotEqual(rejected.SecurityStamp, again.SecurityStamp);

        var application = await ApplicationOfAsync(factory, again);
        Assert.Equal(ApplicationStatus.Pending, application.Status);
        Assert.True(application.Flags.HasFlag(ApplicationConcerns.Reapplication));
        Assert.Equal(1, application.ReapplicationCount);
        Assert.Equal("New Agency", application.AgencyName);
        Assert.Equal(777, application.MembershipNumber.Value);
        Assert.Equal(MembershipClass.Associate, application.MembershipClass);
        Assert.Null(application.DecisionReason);
        Assert.Equal(1, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "MemberReapplied")));
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ApplicationFlagged" && e.Detail == "Reapplication")));
    }

    [Fact]
    public async Task A_reapplication_shows_up_for_reviewers_with_its_flag_and_must_be_acknowledged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var first = await SignUps.ApplyAsync(factory);
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RejectApplication(first));
        await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory, Email));

        var withoutFlag = await factory.ProposeAsync(admins[0], Proposals.Approve(first, newAgencyName: "Copperbelt Homes"));
        var withFlag = await factory.ProposeAsync(admins[0], Proposals.Approve(first, newAgencyName: "Copperbelt Homes", acknowledgedFlags: "Reapplication"));

        Assert.Equal(ProposeOutcome.Refused, withoutFlag.Outcome);
        Assert.Equal(["FlagNotAcknowledged"], withoutFlag.Problems!.Select(problem => problem.Code));
        Assert.Equal(ProposeOutcome.Proposed, withFlag.Outcome);
    }

    // --- A duplicate membership number ---------------------------------------------------------------------------

    [Fact]
    public async Task A_membership_number_another_member_already_holds_still_creates_the_application_but_flags_it()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("holder@example.test", membershipNumber: 884);

        var response = await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory, membershipNumber: "AGT 884"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // the same answer as ever
        var application = await ApplicationOfAsync(factory, await ApplicantAsync(factory));
        Assert.Equal(ApplicationStatus.Pending, application.Status);
        Assert.Equal(ApplicationConcerns.DuplicateMembershipNumber, application.Flags);
        Assert.Equal(1, await factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ApplicationFlagged" && e.Detail == "DuplicateMembershipNumber")));
        var signedUp = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().SingleAsync(e => e.Action == "MemberSignedUp"));
        Assert.Contains("DuplicateMembershipNumber", signedUp.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_waiting_applications_for_one_number_both_show_the_flag_to_reviewers()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await SignUps.ApplyAsync(factory, "first@example.test", SignUps.Body(factory, "first@example.test", membershipNumber: "AGT 884"));
        await SignUps.ApplyAsync(factory, "second@example.test", SignUps.Body(factory, "second@example.test", membershipNumber: "AGT 884"));

        var stored = await factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().OrderBy(a => a.SubmittedAt).ThenBy(a => a.Id).ToListAsync());
        using var scope = factory.Services.CreateScope();
        var listed = await scope.ServiceProvider.GetRequiredService<IApplicationReviewService>().ListPendingAsync();

        Assert.Single(stored, application => application.Flags == ApplicationConcerns.DuplicateMembershipNumber); // only the later one was flagged when it was made
        Assert.Equal(2, listed.Count);
        Assert.All(listed, application => Assert.Equal(["DuplicateMembershipNumber"], application.Flags)); // flagged live, for both
    }

    [Fact]
    public async Task A_rejected_applicants_claim_on_a_number_does_not_flag_anyone_else()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        var first = await SignUps.ApplyAsync(factory, "first@example.test", SignUps.Body(factory, "first@example.test", membershipNumber: "AGT 884"));
        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RejectApplication(first));

        await SignUps.ApplyAsync(factory, "second@example.test", SignUps.Body(factory, "second@example.test", membershipNumber: "AGT 884"));

        var application = await ApplicationOfAsync(factory, await ApplicantAsync(factory, "second@example.test"));
        Assert.Equal(ApplicationConcerns.None, application.Flags);
    }

    // --- Bots, limits, tokens -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_bot_check_is_refused_before_anything_else_happens()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var hashes = factory.PasswordVerifications.Hashes;

        var response = await SignUps.PostAsync(factory.CreateApiClient(), SignUps.Body(factory, turnstileToken: "not-valid"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(hashes, factory.PasswordVerifications.Hashes);
        Assert.Empty(factory.Breaches.CheckedPasswords);
        Assert.Equal("not-valid", Assert.Single(factory.Turnstile.Calls).Token);
    }

    [Fact]
    public async Task Sign_up_is_limited_to_three_an_hour_per_address_whatever_the_outcome()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, signUpPerIpPerHour: 3);
        var client = factory.CreateApiClient();

        var first = await SignUps.PostAsync(client, SignUps.Body(factory, "a@example.test"), forwardedFor: "203.0.113.7");
        var second = await SignUps.PostAsync(client, SignUps.Body(factory, "not-an-email"), forwardedFor: "203.0.113.7"); // a mistake counts
        var third = await SignUps.PostAsync(client, SignUps.Body(factory, "a@example.test"), forwardedFor: "203.0.113.7"); // so does an existing account
        var fourth = await SignUps.PostAsync(client, SignUps.Body(factory, "d@example.test"), forwardedFor: "203.0.113.7");
        var elsewhere = await SignUps.PostAsync(client, SignUps.Body(factory, "e@example.test"), forwardedFor: "203.0.113.8");

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.OK], [first.StatusCode, second.StatusCode, third.StatusCode]);
        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.True(fourth.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode); // another address has its own allowance
        Assert.Null(await factory.WithDbAsync(db => db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == "d@example.test")));
    }

    [Fact]
    public void The_default_limits_are_three_sign_ups_an_hour_and_ten_setup_calls_a_minute()
    {
        Assert.Equal(3, new CaseRegister.Api.Security.RateLimitingOptions().SignUpPerIpPerHour);
        Assert.Equal(10, new CaseRegister.Api.Security.RateLimitingOptions().StaffSetupPerIpPerMinute);
    }

    [Fact]
    public async Task Signing_up_needs_the_anti_forgery_token()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var response = await factory.CreateApiClient().SendAsync(HttpMethod.Post, "/api/auth/sign-up", SignUps.Body(factory), withCsrfToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    // --- The log --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sign_up_is_logged_with_its_flags_and_nothing_personal()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var user = await SignUps.ApplyAsync(factory, body: SignUps.Body(factory, Email, membershipNumber: "AGT 31337"));

        var entry = Assert.Single(await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "MemberSignedUp").ToListAsync()));
        Assert.Equal(user.Id, entry.ActorUserId);
        Assert.Equal(user.Id.ToString(), entry.Subject);
        Assert.Contains("flags: none", entry.Detail, StringComparison.Ordinal);
        var everything = string.Join('|', await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Subject + "|" + e.Detail + "|" + e.IpAddress).ToListAsync()));
        foreach (var secret in new[] { Email, "31337", SignUps.StrongPassword, "Jane Mwansa", "Copperbelt" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }
}
