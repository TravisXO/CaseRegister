using CaseRegister.Api.Cli;
using CaseRegister.Application.Applications;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Seeding;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>The demo data for trying the flow by hand: made only in Development, made once, and all of it made up.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class DemoSeedTests(PostgresFixture postgres)
{
    private static DemoApplicationsSeeder SeederFor(IServiceScope scope, string environment) =>
        ActivatorUtilities.CreateInstance<DemoApplicationsSeeder>(scope.ServiceProvider, new FakeHostEnvironment(environment));

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task The_command_refuses_outside_development_and_changes_nothing(string environment)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, environmentName: environment);
        var terminal = new ScriptedTerminal([], []);

        var exitCode = await DemoCommands.RunAsync(["seed-demo"], factory.Services, terminal);

        Assert.Equal(DemoCommands.Refused, exitCode);
        Assert.Contains(terminal.Output, line => line.Contains("only runs in the Development environment", StringComparison.Ordinal) && line.Contains(environment, StringComparison.Ordinal));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Theory]
    [InlineData("Testing")]
    [InlineData("Production")]
    public async Task The_seeder_itself_also_refuses_outside_development(string environment)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => SeederFor(scope, environment).SeedAsync());

        Assert.Contains("Development", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
    }

    [Fact]
    public async Task The_command_rejects_extra_arguments()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(DemoCommands.Misused, await DemoCommands.RunAsync(["seed-demo", "--everything"], factory.Services, new ScriptedTerminal([], [])));
    }

    [Fact]
    public async Task In_development_it_makes_agencies_and_pending_applications_a_reviewer_can_see()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();

        var result = await SeederFor(scope, "Development").SeedAsync();

        Assert.Equal(new DemoSeedResult(4, 5), result);
        var users = await factory.WithDbAsync(db => db.Users.AsNoTracking().ToListAsync());
        Assert.Equal(5, users.Count);
        Assert.All(users, user =>
        {
            Assert.Equal(AccountStatus.Pending, user.AccountStatus);
            Assert.EndsWith("@example.test", user.Email!, StringComparison.Ordinal); // a reserved domain: never a real person
            Assert.False(user.TwoFactorEnabled);
        });

        var views = await scope.ServiceProvider.GetRequiredService<IApplicationReviewService>().ListPendingAsync();
        Assert.Equal(5, views.Count);
        Assert.Contains(views, view => view.SuggestedAgencies.Count == 1 && view.AgencyName.EndsWith(" Ltd", StringComparison.Ordinal)); // a likely match to show
        Assert.Contains(views, view => view.Flags.Contains("DuplicateMembershipNumber")); // a flag to acknowledge
        Assert.Contains(views, view => view.SuggestedAgencies.Count == 0 && view.AgencyName.EndsWith("Prime Realty", StringComparison.Ordinal)); // a new agency
        Assert.All(views, view => Assert.Equal(DateTimeOffset.UtcNow.AddHours(2).Year, view.CertificateYear));
    }

    [Fact]
    public async Task Running_it_again_adds_nothing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        await SeederFor(scope, "Development").SeedAsync();

        var again = await SeederFor(scope, "Development").SeedAsync();

        Assert.Equal(new DemoSeedResult(0, 0), again);
        Assert.Equal(4, await factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Equal(5, await factory.WithDbAsync(db => db.MemberApplications.CountAsync()));
    }

    [Fact]
    public async Task The_made_up_names_are_the_same_every_time()
    {
        await using var first = await ApiFactory.CreateAsync(postgres);
        await using var second = await ApiFactory.CreateAsync(postgres);
        using var firstScope = first.Services.CreateScope();
        using var secondScope = second.Services.CreateScope();

        await SeederFor(firstScope, "Development").SeedAsync();
        await SeederFor(secondScope, "Development").SeedAsync();

        var one = await first.WithDbAsync(db => db.Agencies.AsNoTracking().OrderBy(agency => agency.Name).Select(agency => agency.Name).ToListAsync());
        var two = await second.WithDbAsync(db => db.Agencies.AsNoTracking().OrderBy(agency => agency.Name).Select(agency => agency.Name).ToListAsync());
        Assert.Equal(one, two);
    }

    [Fact]
    public async Task Each_made_up_account_is_in_the_audit_log_and_the_chain_stays_intact()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();

        await SeederFor(scope, "Development").SeedAsync();

        Assert.Equal(5, (await factory.AuditEntriesAsync()).Count(entry => entry.Action == "MemberSignedUp"));
        Assert.True((await factory.VerifyAuditAsync()).Intact);
    }
}
