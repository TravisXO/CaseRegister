using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Seeding;

/// <summary>
/// Creates a few fake demo accounts for local development. It refuses to run anywhere but
/// Development, and it has no built-in password: the password comes from user-secrets
/// (Seed:DemoPassword), and with none configured nothing is created.
/// </summary>
public sealed partial class DemoDataSeeder(
    IHostEnvironment environment,
    UserManager<ApplicationUser> users,
    IConfiguration configuration,
    ILogger<DemoDataSeeder> logger)
{
    public const string PasswordKey = "Seed:DemoPassword";

    // example.test is a reserved domain: these addresses can never belong to a real person.
    private static readonly (string Email, string FullName, int Number, AccountStatus Status)[] _accounts =
    [
        ("demo.approved@example.test", "Demo Approved Agent", 101, AccountStatus.Approved),
        ("demo.pending@example.test", "Demo Pending Agent", 102, AccountStatus.Pending),
    ];

    public async Task SeedAsync()
    {
        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"Demo data can only be seeded in the Development environment, not in '{environment.EnvironmentName}'.");
        }

        var password = configuration[PasswordKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            LogSkipped(logger, PasswordKey);
            return;
        }

        foreach (var (email, fullName, number, status) in _accounts)
        {
            if (await users.FindByEmailAsync(email) is not null)
            {
                continue;
            }

            var result = await users.CreateAsync(
                ApplicationUser.Create(email, fullName, MembershipNumber.FromValue(number), status),
                password);

            if (!result.Succeeded)
            {
                LogFailed(logger, email, string.Join(',', result.Errors.Select(error => error.Code)));
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No demo accounts created: {Key} is not set.")]
    private static partial void LogSkipped(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo account {Email} was not created: {Codes}")]
    private static partial void LogFailed(ILogger logger, string email, string codes);
}
