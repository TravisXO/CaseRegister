using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Common;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Accounts;

/// <summary>When records may be removed, decided in one class. Nothing is removed; these are only the dates.</summary>
public class RetentionPolicyTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 19, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_suspended_account_is_kept_for_ten_years_from_the_day_it_was_suspended()
    {
        Assert.Equal(new DateTimeOffset(2036, 10, 19, 9, 30, 0, TimeSpan.Zero), RetentionPolicy.RetainUntil(AccountStatus.Suspended, _now));
    }

    [Fact]
    public void A_rejected_application_is_kept_for_twelve_months_from_the_decision()
    {
        Assert.Equal(new DateTimeOffset(2027, 10, 19, 9, 30, 0, TimeSpan.Zero), RetentionPolicy.RetainUntil(AccountStatus.Rejected, _now));
    }

    [Theory]
    [InlineData(AccountStatus.Pending)]
    [InlineData(AccountStatus.Approved)]
    public void Nothing_is_scheduled_while_an_account_is_waiting_or_current(AccountStatus status)
    {
        Assert.Null(RetentionPolicy.RetainUntil(status, _now));
    }

    [Fact]
    public void The_ten_years_do_not_drift_on_a_leap_day()
    {
        var leapDay = new DateTimeOffset(2028, 2, 29, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2038, 2, 28, 12, 0, 0, TimeSpan.Zero), RetentionPolicy.RetainUntil(AccountStatus.Suspended, leapDay));
        Assert.Equal(new DateTimeOffset(2029, 2, 28, 12, 0, 0, TimeSpan.Zero), RetentionPolicy.RetainUntil(AccountStatus.Rejected, leapDay));
    }

    [Fact]
    public void A_suspended_agency_is_kept_for_ten_years_and_an_active_one_has_no_date()
    {
        Assert.Equal(new DateTimeOffset(2036, 10, 19, 9, 30, 0, TimeSpan.Zero), RetentionPolicy.RetainUntil(AgencyStatus.Suspended, _now));
        Assert.Null(RetentionPolicy.RetainUntil(AgencyStatus.Active, _now));
    }

    [Fact]
    public void Removal_may_be_considered_only_when_there_is_a_date_it_has_come_and_nothing_holds_the_record()
    {
        var due = _now.AddDays(-1);

        Assert.True(RetentionPolicy.MayBeConsideredForRemoval(due, legalHold: false, _now));
        Assert.True(RetentionPolicy.MayBeConsideredForRemoval(_now, legalHold: false, _now)); // the day itself
        Assert.False(RetentionPolicy.MayBeConsideredForRemoval(_now.AddSeconds(1), legalHold: false, _now)); // not yet
        Assert.False(RetentionPolicy.MayBeConsideredForRemoval(null, legalHold: false, _now)); // nothing scheduled
        Assert.False(RetentionPolicy.MayBeConsideredForRemoval(due, legalHold: true, _now)); // held, whatever the date
        Assert.False(RetentionPolicy.MayBeConsideredForRemoval(null, legalHold: true, _now));
    }

    [Fact]
    public void The_privacy_notice_and_the_record_of_processing_quote_the_same_periods_as_the_policy()
    {
        var notice = File.ReadAllText(Path.Combine(SolutionGraph.SolutionRoot, "frontend", "src", "pages", "PrivacyPage.tsx"));
        var record = File.ReadAllText(Path.Combine(SolutionGraph.SolutionRoot, "docs", "ropa.md"));

        Assert.Contains($"At least {RetentionPolicy.RelationshipRecordYears} years after the relationship ends", notice, StringComparison.Ordinal);
        Assert.Contains($"{RetentionPolicy.RejectedApplicationMonths} months from the decision", notice, StringComparison.Ordinal);
        Assert.Contains($"**{RetentionPolicy.RelationshipRecordYears} years**", record, StringComparison.Ordinal);
        Assert.Contains($"**{RetentionPolicy.RejectedApplicationMonths} months**", record, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agency_sets_and_clears_its_own_date_as_it_is_suspended_and_reactivated()
    {
        var agency = Agency.Create("Copperbelt Homes");
        Assert.Null(agency.RetainUntil);

        agency.Suspend(_now);
        Assert.Equal(_now.AddYears(10), agency.RetainUntil);

        agency.Reactivate(_now.AddDays(3));
        Assert.Null(agency.RetainUntil);
        Assert.False(agency.LegalHold);
    }
}

[Collection(PostgresTestGroup.Name)]
public class RetentionRecordedTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Suspending_and_reactivating_an_account_sets_and_clears_the_date_and_a_legal_hold_is_never_touched()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        await factory.WithDbAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET legal_hold = true WHERE email = 'member@example.test'");

            return 0;
        });
        Assert.Null(await RetainUntilAsync(factory, member.Email!));

        Assert.Equal(AdminActionOutcome.Done, (await factory.SuspendUserAsync(admins[0], member)).Outcome);

        var suspended = factory.Clock.GetUtcNow();
        Assert.Equal(RetentionPolicy.RetainUntil(AccountStatus.Suspended, suspended).GetValueOrDefault().UtcDateTime, (await RetainUntilAsync(factory, member.Email!))!.Value.UtcDateTime, TimeSpan.FromSeconds(2));

        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.ForUser(PendingActionType.ReactivateUser, member));

        Assert.Null(await RetainUntilAsync(factory, member.Email!));
        Assert.True(await factory.WithDbAsync(db => db.Database.SqlQueryRaw<bool>("SELECT legal_hold AS \"Value\" FROM users WHERE email = 'member@example.test'").SingleAsync()));
    }

    [Fact]
    public async Task Suspending_and_reactivating_an_agency_sets_and_clears_the_date()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(3);
        var agency = await factory.CreateAgencyAsync();

        Assert.Equal(AdminActionOutcome.Done, (await factory.SuspendAgencyAsync(admins[0], agency.Id)).Outcome);
        var held = await factory.WithDbAsync(db => db.Agencies.AsNoTracking().SingleAsync(a => a.Id == agency.Id));
        Assert.NotNull(held.RetainUntil);
        Assert.Equal(factory.Clock.GetUtcNow().AddYears(10).UtcDateTime, held.RetainUntil!.Value.UtcDateTime, TimeSpan.FromSeconds(2));

        await factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.ForAgency(PendingActionType.ReactivateAgency, agency.Id));
        Assert.Null((await factory.WithDbAsync(db => db.Agencies.AsNoTracking().SingleAsync(a => a.Id == agency.Id))).RetainUntil);
    }

    private static Task<DateTimeOffset?> RetainUntilAsync(ApiFactory factory, string email) =>
        factory.WithDbAsync(db => db.Users.AsNoTracking().Where(user => user.Email == email).Select(user => user.RetainUntil).SingleAsync());
}
