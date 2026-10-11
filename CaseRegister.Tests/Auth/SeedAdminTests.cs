using System.Net;
using CaseRegister.Api.Cli;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CaseRegister.Tests.Auth;

/// <summary>A terminal that answers from a script and remembers everything it was asked and told.</summary>
internal sealed class ScriptedTerminal(IEnumerable<string> lines, IEnumerable<string> secrets, bool interactive = true) : ITerminal
{
    private readonly Queue<string> _lines = new(lines);
    private readonly Queue<string> _secrets = new(secrets);

    public bool IsInteractive { get; } = interactive;

    public List<string> Output { get; } = [];

    public List<string> LinePrompts { get; } = [];

    public List<string> SecretPrompts { get; } = [];

    public string? ReadLine(string prompt)
    {
        LinePrompts.Add(prompt);

        return _lines.Count > 0 ? _lines.Dequeue() : null;
    }

    public string? ReadSecret(string prompt)
    {
        SecretPrompts.Add(prompt);

        return _secrets.Count > 0 ? _secrets.Dequeue() : null;
    }

    public void WriteLine(string text) => Output.Add(text);

    public string Everything => string.Join('\n', Output.Concat(LinePrompts).Concat(SecretPrompts));
}

/// <summary><c>dotnet run -- seed-admin</c>: the first admins, created safely and only while fewer than two exist.</summary>
[Collection(PostgresTestGroup.Name)]
public class SeedAdminTests(PostgresFixture postgres)
{
    private const string Email = "first.admin@example.test";
    private const string FullName = "First Admin";
    private const string StrongPassword = "purple tractor sings loudly";

    private static IConfiguration ConfigurationFor(ApiFactory factory, bool withMigrations = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"],
            ["ConnectionStrings:Migrations"] = withMigrations ? factory.OwnerConnectionString : null,
        };

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static Task<int> RunAsync(ApiFactory factory, ScriptedTerminal terminal, string[]? args = null, bool withMigrations = true) =>
        SeedAdminCommand.RunAsync(args ?? [SeedAdminCommand.Name], factory.Services, ConfigurationFor(factory, withMigrations), terminal);

    private static ScriptedTerminal Answers(string yes = "yes", string email = Email, string password = StrongPassword) =>
        new([email, FullName, yes], [password, password]);

    private static Task<int> AdminCount(ApiFactory factory) =>
        factory.WithDbAsync(db => db.UserRoles.CountAsync(userRole => userRole.RoleId == CaseRegister.Infrastructure.Persistence.RoleSeed.IdOf(Role.Admin)));

    private static Task<int> AuditCount(ApiFactory factory, string action) =>
        factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action));

    // --- The normal run ---------------------------------------------------------------------------------

    [Fact]
    public async Task It_creates_an_approved_admin_with_no_agency_no_membership_and_no_authenticator()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = Answers();

        var exitCode = await RunAsync(factory, terminal);

        Assert.Equal(SeedAdminCommand.Succeeded, exitCode);
        var admin = await factory.GetUserAsync(Email);
        Assert.Equal(FullName, admin.FullName);
        Assert.Equal(AccountStatus.Approved, admin.AccountStatus);
        Assert.Null(admin.AgencyId);
        Assert.Null(admin.MembershipNumber);
        Assert.Null(admin.MembershipClass);
        Assert.False(admin.TwoFactorEnabled);
        Assert.Equal([Role.Admin], await factory.RolesOfAsync(admin));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == admin.Id)));
        Assert.Equal(1, await AuditCount(factory, "AdminSeeded"));
    }

    [Fact]
    public async Task The_new_admin_has_to_enrol_in_two_factor_at_first_sign_in_and_nothing_else_works_until_then()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await RunAsync(factory, Answers());
        var admin = await factory.GetUserAsync(Email);

        var client = factory.CreateApiClient();
        var signIn = await client.SignInAsync(Email, StrongPassword);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(signIn));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/pending-actions")).StatusCode);

        await TwoFactorFlows.EnrolFullyAsync(factory, client, admin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/pending-actions")).StatusCode);
    }

    [Fact]
    public async Task The_password_is_only_asked_for_at_a_masked_prompt_and_is_never_shown_or_logged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = Answers();

        await RunAsync(factory, terminal);

        Assert.Equal(2, terminal.SecretPrompts.Count); // the password, and again
        Assert.DoesNotContain(terminal.LinePrompts, prompt => prompt.Contains("assword", StringComparison.Ordinal)); // never read as a plain line
        Assert.DoesNotContain(StrongPassword, terminal.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Logs.Entries, entry => entry.Message.Contains(StrongPassword, StringComparison.Ordinal));
        Assert.DoesNotContain(
            await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Detail + "|" + e.Subject).ToListAsync()),
            text => text.Contains(StrongPassword, StringComparison.Ordinal));
    }

    [Fact]
    public async Task It_shows_which_database_host_it_will_use_and_never_the_password_in_the_connection()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = Answers();
        var connection = new NpgsqlConnectionStringBuilder(factory.Services.GetRequiredService<IConfiguration>()["ConnectionStrings:Default"]);

        await RunAsync(factory, terminal);

        var line = Assert.Single(terminal.Output, text => text.StartsWith("This will create an admin on database host", StringComparison.Ordinal));
        Assert.Contains($"'{connection.Host}'", line, StringComparison.Ordinal);
        Assert.Contains($"'{connection.Database}'", line, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Password!, terminal.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Username!, terminal.Everything, StringComparison.Ordinal);
        Assert.Equal("Type \"yes\" to continue: ", terminal.LinePrompts.Last());
    }

    [Fact]
    public void Only_the_host_and_database_are_ever_taken_from_a_connection_string()
    {
        var (host, database) = SeedAdminCommand.DatabaseOf("Host=ep-quiet-db.example.com;Port=5432;Database=neondb;Username=owner;Password=s3cret;SSL Mode=VerifyFull");

        Assert.Equal("ep-quiet-db.example.com", host);
        Assert.Equal("neondb", database);
        Assert.Equal(("(not configured)", "(not configured)"), SeedAdminCommand.DatabaseOf(null));
        Assert.Equal(("(not configured)", "(not configured)"), SeedAdminCommand.DatabaseOf("  "));
    }

    // --- Typing "yes" ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("y")]
    [InlineData("Yes")]
    [InlineData("YES")]
    [InlineData("no")]
    [InlineData("yes please")]
    [InlineData("ok")]
    public async Task Anything_but_yes_creates_nothing(string answer)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var exitCode = await RunAsync(factory, Answers(yes: answer));

        Assert.Equal(SeedAdminCommand.Refused, exitCode);
        Assert.Equal(0, await AdminCount(factory));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(1, await AuditCount(factory, "AdminSeedRefused"));
        Assert.Equal(0, await AuditCount(factory, "AdminSeeded"));
    }

    [Fact]
    public async Task No_answer_at_all_is_not_a_yes()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = new ScriptedTerminal([Email, FullName], [StrongPassword, StrongPassword]); // the input ends

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, terminal));
        Assert.Equal(0, await AdminCount(factory));
    }

    [Fact]
    public async Task Yes_with_spaces_around_it_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, Answers(yes: "  yes  ")));
    }

    // --- The password rules ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_password_rules_of_every_account_apply_and_a_bad_password_is_asked_for_again()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.BreachedPasswords.Add("password1234567");
        var terminal = new ScriptedTerminal(
            [Email, FullName, "yes"],
            [
                "short", "short", // too short
                "password1234567", "password1234567", // in a breach
                StrongPassword, StrongPassword,
            ]);

        var exitCode = await RunAsync(factory, terminal);

        Assert.Equal(SeedAdminCommand.Succeeded, exitCode);
        Assert.Contains(terminal.Output, text => text.Contains("12", StringComparison.Ordinal)); // the length rule was explained
        Assert.Equal(6, terminal.SecretPrompts.Count);
        Assert.Contains(StrongPassword, factory.Breaches.CheckedPasswords); // the breach check ran, as for any account
        Assert.NotNull(await factory.GetUserAsync(Email));
    }

    [Fact]
    public async Task A_password_made_from_the_persons_own_details_is_not_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = new ScriptedTerminal(
            [Email, FullName, "yes"],
            ["first.admin first.admin", "first.admin first.admin", StrongPassword, StrongPassword]);

        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, terminal));
        Assert.Equal(4, terminal.SecretPrompts.Count); // the first was turned away
        Assert.NotEmpty(terminal.Output);
    }

    [Fact]
    public async Task Three_unacceptable_passwords_end_the_run_without_creating_anyone()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = new ScriptedTerminal([Email, FullName, "yes"], ["short", "short", "short", "short", "short", "short"]);

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, terminal));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(1, await AuditCount(factory, "AdminSeedRefused"));
    }

    [Fact]
    public async Task A_confirmation_that_does_not_match_is_not_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = new ScriptedTerminal(
            [Email, FullName, "yes"],
            [StrongPassword, "something else entirely", StrongPassword, StrongPassword]);

        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, terminal));
        Assert.Contains(terminal.Output, text => text.Contains("not the same", StringComparison.Ordinal));
        Assert.Equal(4, terminal.SecretPrompts.Count);
    }

    [Fact]
    public async Task An_email_already_in_use_creates_nothing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync(Email);

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, Answers()));
        Assert.Equal(0, await AdminCount(factory));
    }

    [Fact]
    public async Task A_missing_email_or_name_creates_nothing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, new ScriptedTerminal(["", FullName, "yes"], [StrongPassword, StrongPassword])));
        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, new ScriptedTerminal([Email, "  ", "yes"], [StrongPassword, StrongPassword])));
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    // --- Never from the command line ----------------------------------------------------------------------------

    [Theory]
    [InlineData("--password", "purple tractor sings loudly")]
    [InlineData("--password=purple tractor sings loudly", "")]
    [InlineData("purple tractor sings loudly", "")]
    [InlineData("--email", "someone@example.test")]
    public async Task Nothing_is_accepted_as_an_argument_least_of_all_a_password(string first, string second)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = Answers();
        var arguments = new[] { SeedAdminCommand.Name, first, second }.Where(argument => argument.Length > 0).ToArray();

        var exitCode = await RunAsync(factory, terminal, arguments);

        Assert.Equal(SeedAdminCommand.Refused, exitCode);
        Assert.Empty(terminal.LinePrompts); // it did not even ask
        Assert.Empty(terminal.SecretPrompts);
        Assert.DoesNotContain(StrongPassword, terminal.Everything, StringComparison.Ordinal);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(1, await AuditCount(factory, "AdminSeedRefused"));
    }

    [Fact]
    public async Task It_refuses_when_input_is_not_a_terminal_where_a_password_cannot_be_masked()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = new ScriptedTerminal([Email, FullName, "yes"], [StrongPassword, StrongPassword], interactive: false);

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, terminal));
        Assert.Empty(terminal.SecretPrompts);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    // --- Migrations -------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_refuses_while_migrations_are_pending()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        // Make the newest migration look as if it had not been applied.
        await PostgresFixture.ExecuteAsync(
            factory.OwnerConnectionString!,
            "DELETE FROM __ef_migrations_history WHERE \"migration_id\" = (SELECT max(\"migration_id\") FROM __ef_migrations_history)");
        var terminal = Answers();

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, terminal));
        Assert.Contains(terminal.Output, text => text.Contains("migrations", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(terminal.LinePrompts);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
        Assert.Equal(1, await AuditCount(factory, "AdminSeedRefused"));
    }

    [Fact]
    public async Task It_runs_when_every_migration_has_been_applied()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.False(await CaseRegister.Infrastructure.Persistence.DatabaseMigrator.HasPendingMigrationsAsync(factory.OwnerConnectionString!));
        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, Answers()));
    }

    [Fact]
    public async Task It_refuses_when_it_cannot_tell_whether_migrations_are_pending()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var terminal = Answers();

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, terminal, withMigrations: false));
        Assert.Empty(terminal.LinePrompts);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    // --- Only while fewer than two admins exist -----------------------------------------------------------------

    [Fact]
    public async Task It_works_for_the_first_and_the_second_admin_and_then_never_again()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, Answers(email: "one@example.test")));
        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, Answers(email: "two@example.test")));

        var third = Answers(email: "three@example.test");
        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, third));
        Assert.Contains(third.Output, text => text.Contains("already 2 active admins", StringComparison.Ordinal));
        Assert.Empty(third.LinePrompts);
        Assert.Equal(2, await AdminCount(factory));
    }

    [Fact]
    public async Task Admins_made_any_other_way_count_too()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateAdminsAsync(2);

        Assert.Equal(SeedAdminCommand.Refused, await RunAsync(factory, Answers()));
        Assert.Equal(2, await AdminCount(factory));
    }

    [Fact]
    public async Task A_suspended_admin_does_not_count_so_the_command_can_replace_them()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var admins = await factory.CreateAdminsAsync(2);
        await factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == admins[1].Id)).AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });

        Assert.Equal(SeedAdminCommand.Succeeded, await RunAsync(factory, Answers()));
    }

    [Fact]
    public async Task Two_runs_at_the_same_moment_cannot_both_add_an_admin_to_a_system_with_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateAdminsAsync(1);

        for (var round = 0; round < 3; round++)
        {
            // Reset to one admin for each round.
            await factory.WithDbAsync(async db =>
            {
                var extra = await db.Users.Where(u => u.Email!.StartsWith("race")).ToListAsync();
                foreach (var user in extra)
                {
                    user.AccountStatus = AccountStatus.Suspended;
                }

                await db.SaveChangesAsync();

                return 0;
            });

            var gate = new TaskCompletionSource();
            Task<SeedAdminResult> Seed(string email) => Task.Run(async () =>
            {
                await gate.Task;
                using var scope = factory.Services.CreateScope();

                return await scope.ServiceProvider.GetRequiredService<IAdminSeeder>().SeedAsync(new SeedAdminRequest($"race{round}{email}@example.test", "Racing Admin", StrongPassword));
            });

            var first = Seed("a");
            var second = Seed("b");
            gate.SetResult();
            var outcomes = (await Task.WhenAll(first, second)).Select(result => result.Outcome).Order().ToArray();

            Assert.Equal([SeedAdminOutcome.Created, SeedAdminOutcome.EnoughAdmins], outcomes);
        }
    }

    // --- Every run is logged -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_run_is_in_the_audit_log_whether_or_not_it_created_anyone()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await RunAsync(factory, Answers(yes: "no")); // declined
        await RunAsync(factory, Answers(), [SeedAdminCommand.Name, "--password", "x"]); // arguments
        await RunAsync(factory, Answers(email: "one@example.test")); // created
        await RunAsync(factory, Answers(email: "two@example.test")); // created
        await RunAsync(factory, Answers(email: "three@example.test")); // enough admins

        var entries = await factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "AdminSeeded" || e.Action == "AdminSeedRefused").OrderBy(e => e.CreatedAt).ToListAsync());
        Assert.Equal(["AdminSeedRefused", "AdminSeedRefused", "AdminSeeded", "AdminSeeded", "AdminSeedRefused"], entries.Select(e => e.Action));
        Assert.Contains("not confirmed", entries[0].Detail, StringComparison.Ordinal);
        Assert.Contains("arguments were given", entries[1].Detail, StringComparison.Ordinal);
        Assert.Contains("active admins before: 0", entries[2].Detail, StringComparison.Ordinal);
        Assert.Contains("active admins before: 1", entries[3].Detail, StringComparison.Ordinal);
        Assert.Contains("2 active admins already", entries[4].Detail, StringComparison.Ordinal);
        Assert.All(entries, entry => Assert.Null(entry.ActorUserId)); // run from a command line: no signed-in actor

        var created = await factory.GetUserAsync("one@example.test");
        Assert.Equal(created.Id.ToString(), entries[2].Subject);
    }
}
