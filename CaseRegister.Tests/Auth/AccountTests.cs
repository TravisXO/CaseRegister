using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Seeding;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class AccountTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Emails_are_normalised_before_they_are_stored()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await factory.CreateUserAsync("  Jane.Doe@EXAMPLE.test ");

        var stored = await factory.GetUserAsync("jane.doe@example.test");
        Assert.Equal("jane.doe@example.test", stored.Email);
        Assert.Equal("jane.doe@example.test", stored.UserName);
    }

    [Fact]
    public async Task An_email_can_be_found_whatever_the_case_it_is_typed_in()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane.doe@example.test");

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.NotNull(await users.FindByEmailAsync("JANE.DOE@Example.TEST"));
        Assert.NotNull(await users.FindByNameAsync(" Jane.Doe@example.test "));
    }

    [Fact]
    public async Task An_account_stored_with_an_unnormalised_email_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            UserName = "Jane@Example.test",
            Email = "Jane@Example.test",
            FullName = "Jane",
            MembershipNumber = MembershipNumber.FromValue(1),
        };
        var result = await users.CreateAsync(user, ApiFactory.Password);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == "EmailNotNormalised");
    }

    [Fact]
    public async Task Two_accounts_cannot_share_an_email_even_with_different_capitals()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane@example.test");

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var duplicate = ApplicationUser.Create("JANE@example.test", "Another Jane", MembershipNumber.FromValue(2));
        var result = await users.CreateAsync(duplicate, ApiFactory.Password);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code is "DuplicateEmail" or "DuplicateUserName");
    }

    [Fact]
    public async Task The_database_itself_enforces_one_account_per_email()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane@example.test");

        // Bypass UserManager's own check and go straight at the table. (UserManager normally fills in
        // the normalised columns; here we do it by hand so only the database can object.)
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            var duplicate = ApplicationUser.Create("jane@example.test", "Duplicate", MembershipNumber.FromValue(3));
            duplicate.NormalizedEmail = "jane@example.test";
            duplicate.NormalizedUserName = "jane@example.test-other";
            db.Users.Add(duplicate);
            await db.SaveChangesAsync();
            return 0;
        }));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(failure.InnerException).SqlState);
    }

    [Fact]
    public async Task The_membership_number_has_a_unique_index_so_two_accounts_cannot_share_one()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("first@example.test", membershipNumber: 884);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var second = ApplicationUser.Create("second@example.test", "Second Member", MembershipNumber.Parse("AGT-884", "AGT"));

        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => users.CreateAsync(second, ApiFactory.Password));

        var postgresError = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
        Assert.Equal("ux_users_membership_number", postgresError.ConstraintName);
    }

    [Fact]
    public async Task The_membership_number_is_stored_as_an_integer_and_read_back_with_the_configured_prefix()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane@example.test", membershipNumber: 884);

        var raw = await factory.WithDbAsync(db => db.Database
            .SqlQuery<int>($"SELECT membership_number AS \"Value\" FROM users")
            .SingleAsync());
        var stored = await factory.GetUserAsync("jane@example.test");

        Assert.Equal(884, raw);
        Assert.Equal("AGT 884", stored.MembershipNumber.Format("AGT"));
    }

    [Fact]
    public async Task The_account_status_is_stored_as_readable_text()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("jane@example.test", AccountStatus.Pending);

        var raw = await factory.WithDbAsync(db => db.Database
            .SqlQuery<string>($"SELECT account_status AS \"Value\" FROM users")
            .SingleAsync());

        Assert.Equal("Pending", raw);
    }

    [Fact]
    public async Task An_account_needs_a_name_and_a_membership_number()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var result = await users.CreateAsync(
            new ApplicationUser { UserName = "jane@example.test", Email = "jane@example.test" },
            ApiFactory.Password);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Code == "FullNameRequired");
        Assert.Contains(result.Errors, error => error.Code == "MembershipNumberRequired");
    }

    [Fact]
    public async Task A_new_account_is_pending_unless_it_is_told_otherwise()
    {
        var user = ApplicationUser.Create("jane@example.test", "Jane", MembershipNumber.FromValue(7));

        Assert.Equal(AccountStatus.Pending, user.AccountStatus);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Account_ids_are_version_7_guids()
    {
        var bytes = new ApplicationUser().Id.ToByteArray(bigEndian: true);

        Assert.Equal(7, bytes[6] >> 4);
        await Task.CompletedTask;
    }

    // --- No default, demo or test users ----------------------------------------------------------

    [Fact]
    public async Task A_fresh_application_has_no_users_at_all()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        _ = factory.CreateApiClient(); // start it

        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    [InlineData("")]
    [InlineData("development ")] // not an exact match
    public async Task Demo_seeding_refuses_to_run_outside_Development(string environmentName)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();

        // The password is configured, so only the environment check can stop it.
        var seeder = new DemoDataSeeder(
            new FakeHostEnvironment(environmentName),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            ConfigWithDemoPassword("correct horse battery staple"),
            NullLogger<DemoDataSeeder>.Instance);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(seeder.SeedAsync);

        Assert.Contains("Development", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task The_real_application_pipeline_never_seeds_outside_Development()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres, settings => settings["Seed:DemoPassword"] = "correct horse battery staple");
        _ = factory.CreateApiClient();

        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task In_Development_with_no_password_configured_nothing_is_created()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var seeder = new DemoDataSeeder(
            new FakeHostEnvironment("Development"),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            new ConfigurationBuilder().Build(),
            NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync();

        Assert.Equal(0, await factory.WithDbAsync(db => db.Users.CountAsync()));
    }

    [Fact]
    public async Task In_Development_with_a_password_it_creates_fake_demo_accounts_once()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var seeder = new DemoDataSeeder(
            new FakeHostEnvironment("Development"),
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            ConfigWithDemoPassword("correct horse battery staple"),
            NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync();
        await seeder.SeedAsync(); // running it again must not duplicate anything

        var users = await factory.WithDbAsync(db => db.Users.AsNoTracking().OrderBy(user => user.MembershipNumber).ToListAsync());
        Assert.Equal(2, users.Count);
        Assert.All(users, user => Assert.EndsWith("@example.test", user.Email, StringComparison.Ordinal));
        Assert.Contains(users, user => user.AccountStatus == AccountStatus.Approved);
        Assert.Contains(users, user => user.AccountStatus == AccountStatus.Pending);
    }

    [Fact]
    public async Task There_is_no_password_for_demo_accounts_in_the_source_code()
    {
        // The seeder has no built-in password: the only way to get a demo account is to supply one.
        var source = await File.ReadAllTextAsync(Path.Combine(
            Architecture.SolutionGraph.SolutionRoot, "CaseRegister.Infrastructure", "Seeding", "DemoDataSeeder.cs"));

        Assert.DoesNotContain("Password = \"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("password = \"", source, StringComparison.Ordinal);
    }

    private static IConfiguration ConfigWithDemoPassword(string password) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [DemoDataSeeder.PasswordKey] = password })
            .Build();
}
