using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Tests.Auth;

/// <summary>The password rules, exercised through the real UserManager and validators.</summary>
[Collection(PostgresTestGroup.Name)]
public class PasswordRuleTests(PostgresFixture postgres)
{
    private static int _nextNumber = 5000;

    private static async Task<IdentityResult> TryCreateAsync(
        ApiFactory factory,
        string password,
        string email = "member@example.test",
        string fullName = "Chanda Phiri",
        int? number = null)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = ApplicationUser.Create(email, fullName, MembershipNumber.FromValue(number ?? Interlocked.Increment(ref _nextNumber)));

        return await users.CreateAsync(user, password);
    }

    private static string[] Codes(IdentityResult result) => [.. result.Errors.Select(error => error.Code)];

    [Fact]
    public async Task A_password_of_11_characters_is_refused_and_12_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var tooShort = await TryCreateAsync(factory, "abcdefghijk", "short@example.test");
        var longEnough = await TryCreateAsync(factory, "abcdefghijkl", "long@example.test");

        Assert.False(tooShort.Succeeded);
        Assert.Contains("PasswordTooShort", Codes(tooShort));
        Assert.True(longEnough.Succeeded, string.Join(',', Codes(longEnough)));
    }

    [Fact]
    public async Task A_password_of_128_characters_is_accepted_and_129_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var maximum = string.Concat(Enumerable.Repeat("abcdefgh", 16));
        Assert.Equal(128, maximum.Length);

        var accepted = await TryCreateAsync(factory, maximum, "max@example.test");
        var refused = await TryCreateAsync(factory, maximum + "x", "over@example.test");

        Assert.True(accepted.Succeeded, string.Join(',', Codes(accepted)));
        Assert.False(refused.Succeeded);
        Assert.Contains("PasswordTooLong", Codes(refused));
    }

    [Theory]
    [InlineData("alllowercaseletterswithnothingelse")]
    [InlineData("ALLUPPERCASELETTERSWITHNOTHINGELSE")]
    [InlineData("493817265049")]
    [InlineData("a long sentence with only spaces")]
    public async Task No_character_types_are_forced(string password)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var result = await TryCreateAsync(factory, password);

        Assert.True(result.Succeeded, string.Join(',', Codes(result)));
    }

    [Theory]
    [InlineData("aaaaaaaaaaaa", false)]
    [InlineData("abababababab", false)]
    [InlineData("abcabcabcabc", false)]
    [InlineData("abcdabcdabcd", true)]
    public async Task At_least_four_different_characters_are_required(string password, bool accepted)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var result = await TryCreateAsync(factory, password);

        Assert.Equal(accepted, result.Succeeded);
        if (!accepted)
        {
            Assert.Contains("PasswordRequiresUniqueChars", Codes(result));
        }
    }

    [Theory]
    [InlineData("jane.banda@example.test-ok")]
    [InlineData("I am JANE.BANDA for sure")]
    [InlineData("bandaFamily2024!!")]
    [InlineData("my-name-is-Jane-ok")]
    [InlineData("AGT 884 forever and ever")]
    [InlineData("agt884isMyNumber")]
    [InlineData("a AGT-884 b c d e f")]
    public async Task A_password_containing_the_email_name_or_membership_number_is_refused(string password)
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var result = await TryCreateAsync(factory, password, "jane.banda@example.test", "Jane Banda", 884);

        Assert.False(result.Succeeded);
        Assert.Contains("PasswordContainsPersonalInfo", Codes(result));
    }

    [Fact]
    public async Task A_password_unrelated_to_the_user_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        var result = await TryCreateAsync(factory, "correct horse battery staple", "jane.banda@example.test", "Jane Banda", 884);

        Assert.True(result.Succeeded, string.Join(',', Codes(result)));
    }

    [Fact]
    public async Task A_breached_password_is_refused()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.BreachedPasswords.Add("a famous leaked password");

        var result = await TryCreateAsync(factory, "a famous leaked password");

        Assert.False(result.Succeeded);
        Assert.Contains("PasswordBreached", Codes(result));
    }

    [Fact]
    public async Task Passwords_that_fail_the_length_rules_are_never_sent_out_for_a_breach_check()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);

        await TryCreateAsync(factory, "short", "a@example.test");
        await TryCreateAsync(factory, new string('x', 129), "b@example.test");

        Assert.Empty(factory.Breaches.CheckedPasswords);
    }

    [Fact]
    public async Task When_the_breach_check_is_unavailable_the_password_is_refused_with_a_try_again_message_and_it_is_logged()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.Unavailable = true;

        var result = await TryCreateAsync(factory, "a perfectly fine password");

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors, e => e.Code == "PasswordCheckUnavailable");
        Assert.Contains("try again shortly", error.Description, StringComparison.OrdinalIgnoreCase);

        var warning = Assert.Single(factory.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("fail closed", StringComparison.Ordinal));
        Assert.DoesNotContain("a perfectly fine password", warning.Message, StringComparison.Ordinal);

        // And nothing was created.
        Assert.Equal(0, await factory.WithDbAsync(async db => await Task.FromResult(db.Users.Count())));
    }

    [Fact]
    public async Task Once_the_breach_check_is_back_the_same_password_is_accepted()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        factory.Breaches.Unavailable = true;
        Assert.False((await TryCreateAsync(factory, "a perfectly fine password")).Succeeded);

        factory.Breaches.Unavailable = false;

        Assert.True((await TryCreateAsync(factory, "a perfectly fine password")).Succeeded);
    }
}
