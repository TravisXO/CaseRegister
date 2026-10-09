using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Database;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CaseRegister.Tests.Testing;

/// <summary>
/// The real application, started in-process for tests: a real Postgres, the real least-privilege
/// role, and controllable stand-ins for the clock, Cloudflare and Have I Been Pwned.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string TurnstileTestSecret = "1x0000000000000000000000000000000AA";
    public const string Password = "correct horse battery staple";

    private readonly Dictionary<string, string?> _settings;

    private ApiFactory(string defaultConnectionString, Action<Dictionary<string, string?>>? adjustSettings)
    {
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = defaultConnectionString,
            ["DataProtection:CertificateBase64"] = TestCertificate.Base64,
            ["DataProtection:CertificatePassword"] = TestCertificate.Password,

            // Cloudflare's published test secret: it accepts every token. The verifier itself is replaced below.
            ["Turnstile:SecretKey"] = TurnstileTestSecret,

            // A neutral test prefix, so no test depends on the real default.
            ["AgentRegistration:DisplayPrefix"] = "AGT",
        };
        adjustSettings?.Invoke(_settings);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // This app instance is finished: give back its database connections now.
        PostgresFixture.ReleaseConnections(_settings.GetValueOrDefault("ConnectionStrings:Default"));
        GC.SuppressFinalize(this);
    }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeBreachedPasswordChecker Breaches { get; } = new();

    public FakeTurnstileVerifier Turnstile { get; } = new();

    public VerificationCounter PasswordVerifications { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Per-IP and per-account sign-in limits. High by default so only the rate-limit tests meet them.</summary>
    public int SignInPerIpPerMinute { get; init; } = 10_000;

    public int SignInPerAccountPerHour { get; init; } = 10_000;

    /// <summary>The application with a database behind it: migrated, with the least-privilege role and its grants.</summary>
    public static async Task<ApiFactory> CreateAsync(
        PostgresFixture postgres,
        Action<Dictionary<string, string?>>? adjustSettings = null,
        int signInPerIpPerMinute = 10_000,
        int signInPerAccountPerHour = 10_000)
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, await File.ReadAllTextAsync(GrantScriptPath));
        PostgresFixture.ReleaseConnections(owner);

        return new ApiFactory(PostgresFixture.AsAppRole(owner), adjustSettings)
        {
            SignInPerIpPerMinute = signInPerIpPerMinute,
            SignInPerAccountPerHour = signInPerAccountPerHour,
        };
    }

    /// <summary>The application with no database behind it, for tests of configuration only. It never connects.</summary>
    public static ApiFactory WithoutDatabase(Action<Dictionary<string, string?>>? adjustSettings = null) =>
        new("Host=localhost;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1", adjustSettings);

    private static string GrantScriptPath => Path.Combine(SolutionGraph.SolutionRoot, "database", "app-role-grants.sql");

    public ApiClient CreateApiClient() => new(this);

    /// <summary>A second, independent instance of the application on the same database, like a second server.</summary>
    public ApiFactory CreateSibling() => new(_settings["ConnectionStrings:Default"]!, settings =>
    {
        foreach (var (key, value) in _settings)
        {
            settings[key] = value;
        }
    });

    public async Task<ApplicationUser> CreateUserAsync(
        string email,
        AccountStatus status = AccountStatus.Approved,
        string password = Password,
        string fullName = "Test Person",
        int agentRegistrationNumber = 0)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Spread numbers out so tests do not collide on the unique registration number.
        var number = agentRegistrationNumber != 0 ? agentRegistrationNumber : Random.Shared.Next(1000, AgentRegistrationNumber.MaxValue);
        var user = ApplicationUser.Create(email, fullName, AgentRegistrationNumber.FromValue(number), status);

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));

        return user;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();

        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task<ApplicationUser> GetUserAsync(string email) =>
        WithDbAsync(db => db.Users.AsNoTracking().SingleAsync(user => user.NormalizedEmail == EmailNormaliser.Normalise(email)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set explicitly: the metadata WebApplicationFactory generates for the content root breaks
        // when the project path contains an apostrophe (as a user folder name can).
        builder.UseContentRoot(Path.Combine(SolutionGraph.SolutionRoot, "CaseRegister.Api"));

        // Not Development: that would load user-secrets and migrate on startup.
        builder.UseEnvironment("Testing");

        foreach (var (key, value) in _settings)
        {
            if (value is not null)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.RemoveAll<IBreachedPasswordChecker>();
            services.AddSingleton<IBreachedPasswordChecker>(Breaches);

            services.RemoveAll<ITurnstileVerifier>();
            services.AddSingleton<ITurnstileVerifier>(Turnstile);

            services.RemoveAll<IPasswordHasher<ApplicationUser>>();
            services.AddSingleton<IPasswordHasher<ApplicationUser>>(provider =>
                new CountingPasswordHasher(provider.GetRequiredService<IOptions<PasswordHasherOptions>>(), PasswordVerifications));

            services.PostConfigure<RateLimitingOptions>(options =>
            {
                options.SignInPerIpPerMinute = SignInPerIpPerMinute;
                options.SignInPerAccountPerHour = SignInPerAccountPerHour;
            });

            // Test-only endpoints (see ProbeController).
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            services.AddAuthorizationBuilder().AddPolicy(ProbeController.NeverPolicy, policy => policy.RequireAssertion(_ => false));
        });
    }
}
