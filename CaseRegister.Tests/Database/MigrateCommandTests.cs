using CaseRegister.Api.Cli;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Auth;
using CaseRegister.Tests.Hardening;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CaseRegister.Tests.Database;

/// <summary>
/// <c>dotnet run -- migrate</c>, which is how production gets its schema: never at startup, and only after a person has seen which
/// database it is and which migrations will run.
/// </summary>
[Collection(PostgresTestGroup.Name)]
public sealed class MigrateCommandTests(PostgresFixture postgres)
{
    private const string FirstMigration = "20261009150724_InitialCreate";

    private static IConfiguration ConfigurationFor(string? migrations) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Migrations"] = migrations }).Build();

    private static Task<int> RunAsync(string? connection, ScriptedTerminal terminal, params string[] extraArguments) =>
        MigrateCommand.RunAsync([MigrateCommand.Name, .. extraArguments], ConfigurationFor(connection), terminal);

    private static ScriptedTerminal Says(string answer = "yes", bool interactive = true) => new([answer], [], interactive);

    private static async Task<bool> HasHistoryAsync(string owner) =>
        await PostgresFixture.ScalarAsync<bool>(owner, "select to_regclass('public.__ef_migrations_history') is not null");

    private static async Task<int> AppliedAsync(string owner) =>
        (int)await PostgresFixture.ScalarAsync<long>(owner, "select count(*) from public.__ef_migrations_history");

    // --- The normal run ---------------------------------------------------------------------------------

    [Fact]
    public async Task It_says_which_database_and_which_migrations_then_applies_them_after_a_typed_yes()
    {
        var owner = await postgres.CreateDatabaseAsync();
        var builder = new NpgsqlConnectionStringBuilder(owner);
        var terminal = Says();
        var all = await DatabaseMigrator.GetPendingMigrationsAsync(owner);

        var exit = await RunAsync(owner, terminal);

        Assert.Equal(MigrateCommand.Succeeded, exit);
        Assert.Contains(terminal.Output, line => line.Contains($"Database host '{builder.Host}', database '{builder.Database}'", StringComparison.Ordinal));
        Assert.Contains(terminal.Output, line => line.Contains($"{all.Count} pending migrations", StringComparison.Ordinal));
        Assert.Contains(terminal.Output, line => line.Trim() == FirstMigration);
        Assert.Contains(terminal.Output, line => line.Contains("backup or a Neon restore point", StringComparison.Ordinal));
        Assert.Contains(terminal.LinePrompts, prompt => prompt.Contains(builder.Database!, StringComparison.Ordinal) && prompt.Contains("yes", StringComparison.Ordinal));
        Assert.Equal(all.Count, await AppliedAsync(owner));
        Assert.Empty(await DatabaseMigrator.GetPendingMigrationsAsync(owner));
    }

    [Fact]
    public async Task The_pending_list_is_only_what_the_database_has_not_had_and_is_in_the_order_it_will_run()
    {
        var owner = await postgres.CreateDatabaseAsync();
        await ApplyUpToAsync(owner, FirstMigration);
        var terminal = Says();

        await RunAsync(owner, terminal);

        var listed = terminal.Output.Where(line => line.StartsWith("  2026", StringComparison.Ordinal)).Select(line => line.Trim()).ToList();
        Assert.DoesNotContain(FirstMigration, listed);
        Assert.NotEmpty(listed);
        Assert.Equal(listed.Order(StringComparer.Ordinal), listed);
        Assert.Equal(1 + listed.Count, await AppliedAsync(owner));
    }

    [Fact]
    public async Task With_nothing_pending_it_says_so_asks_nothing_and_changes_nothing()
    {
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);
        var before = await AppliedAsync(owner);
        var terminal = Says();

        var exit = await RunAsync(owner, terminal);

        Assert.Equal(MigrateCommand.Succeeded, exit);
        Assert.Contains(terminal.Output, line => line.Contains("no pending migrations", StringComparison.Ordinal));
        Assert.Empty(terminal.LinePrompts);
        Assert.Equal(before, await AppliedAsync(owner));
    }

    // --- It refuses unless it is sure --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("y")]
    [InlineData("Yes")]
    [InlineData("YES")]
    [InlineData("yes please")]
    [InlineData("no")]
    public async Task Anything_but_exactly_yes_applies_nothing(string answer)
    {
        var owner = await postgres.CreateDatabaseAsync();
        var terminal = Says(answer);

        var exit = await RunAsync(owner, terminal);

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.Contains(terminal.Output, line => line.Contains("Nothing was applied", StringComparison.Ordinal));
        Assert.False(await HasHistoryAsync(owner));
    }

    [Fact]
    public async Task No_answer_at_all_applies_nothing()
    {
        var owner = await postgres.CreateDatabaseAsync();

        var exit = await RunAsync(owner, new ScriptedTerminal([], []));

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.False(await HasHistoryAsync(owner));
    }

    [Fact]
    public async Task It_refuses_when_input_is_piped_so_nobody_can_script_a_yes()
    {
        var owner = await postgres.CreateDatabaseAsync();
        var terminal = Says(interactive: false);

        var exit = await RunAsync(owner, terminal);

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.Contains(terminal.Output, line => line.Contains("interactive terminal", StringComparison.Ordinal));
        Assert.False(await HasHistoryAsync(owner));
        Assert.Empty(terminal.LinePrompts);
    }

    [Fact]
    public async Task It_takes_no_arguments_because_the_database_comes_from_the_setting_alone()
    {
        var owner = await postgres.CreateDatabaseAsync();
        var terminal = Says();

        var exit = await RunAsync(owner, terminal, "--connection", "anything");

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.False(await HasHistoryAsync(owner));
        Assert.DoesNotContain("anything", terminal.Everything, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task It_refuses_without_the_owner_connection_and_names_the_setting(string? connection)
    {
        var terminal = Says();

        var exit = await RunAsync(connection, terminal);

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.Contains(terminal.Output, line => line.Contains("ConnectionStrings:Migrations", StringComparison.Ordinal));
        Assert.Empty(terminal.LinePrompts);
    }

    [Fact]
    public async Task It_refuses_neons_pooled_address_before_connecting_to_it()
    {
        // Nothing listens there: if it tried to connect, this test would hang or fail on the network rather than on the rule.
        const string pooled = "Host=ep-quiet-sun-123456-pooler.eu-central-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=never-printed-9981;SSL Mode=Require";
        var terminal = Says();

        var exit = await RunAsync(pooled, terminal);

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.Contains(terminal.Output, line => line.Contains("pooled", StringComparison.Ordinal));
        Assert.DoesNotContain("never-printed-9981", terminal.Everything, StringComparison.Ordinal);
        Assert.Empty(terminal.LinePrompts);
    }

    [Fact]
    public async Task A_database_it_cannot_reach_is_reported_by_type_alone_and_nothing_secret_is_printed()
    {
        const string unreachable = "Host=127.0.0.1;Port=1;Database=neondb;Username=neondb_owner;Password=never-printed-7742;Timeout=2";
        var terminal = Says();

        var exit = await RunAsync(unreachable, terminal);

        Assert.Equal(MigrateCommand.Refused, exit);
        Assert.Contains(terminal.Output, line => line.Contains("could not be reached", StringComparison.Ordinal));
        Assert.DoesNotContain("never-printed-7742", terminal.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("neondb_owner", terminal.Everything, StringComparison.Ordinal);
        Assert.Empty(terminal.LinePrompts);
    }

    [Fact]
    public async Task The_password_and_the_user_are_never_shown_only_the_host_and_the_database()
    {
        var owner = await postgres.CreateDatabaseAsync();
        var builder = new NpgsqlConnectionStringBuilder(owner);
        var terminal = Says();

        await RunAsync(owner, terminal);

        Assert.DoesNotContain(builder.Password!, terminal.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain($"Username={builder.Username}", terminal.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", terminal.Everything, StringComparison.Ordinal);
    }

    // --- Production does not do this by itself ------------------------------------------------------------

    [Fact]
    public async Task Starting_the_application_as_production_leaves_an_unmigrated_database_exactly_as_it_was()
    {
        await using var factory = await ApiFactory.CreateUnmigratedAsync(postgres, "Production");
        using var client = factory.Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost");

        using var live = await client.SendAsync(HttpMethod.Get, "/health/live", content: null, (ApiFactory.ProxyHeader, ApiFactory.ProxyTestSecret));

        Assert.True(live.IsSuccessStatusCode);
        Assert.False(await HasHistoryAsync(factory.OwnerConnectionString!));
        Assert.Equal(0L, await PostgresFixture.ScalarAsync<long>(factory.OwnerConnectionString!, "select count(*) from information_schema.tables where table_schema = 'public'"));
    }

    private static async Task ApplyUpToAsync(string owner, string migration)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        AppDbContextOptions.Configure(builder, owner);
        await using var context = new AppDbContext(builder.Options);
        await context.GetService<IMigrator>().MigrateAsync(migration);
    }
}
