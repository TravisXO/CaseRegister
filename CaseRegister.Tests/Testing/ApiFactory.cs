using CaseRegister.Api.Security;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
            ["Membership:DisplayPrefix"] = "AGT",
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

    /// <summary>Two-factor codes tried per user per minute. High by default so only the rate-limit tests meet it.</summary>
    public int TwoFactorAttemptsPerMinute { get; init; } = 10_000;

    /// <summary>The application with a database behind it: migrated, with the least-privilege role and its grants.</summary>
    public static async Task<ApiFactory> CreateAsync(
        PostgresFixture postgres,
        Action<Dictionary<string, string?>>? adjustSettings = null,
        int signInPerIpPerMinute = 10_000,
        int signInPerAccountPerHour = 10_000,
        int twoFactorAttemptsPerMinute = 10_000)
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
            TwoFactorAttemptsPerMinute = twoFactorAttemptsPerMinute,
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

    /// <summary>
    /// Creates an account. By default it already has two-factor switched on (with a key the test can
    /// generate codes for), because that is what almost every account in the real system will have.
    /// </summary>
    public async Task<ApplicationUser> CreateUserAsync(
        string email,
        AccountStatus status = AccountStatus.Approved,
        string password = Password,
        string fullName = "Test Person",
        int membershipNumber = 0,
        bool twoFactor = true)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Spread numbers out so tests do not collide on the unique membership number.
        var number = membershipNumber != 0 ? membershipNumber : Random.Shared.Next(1000, MembershipNumber.MaxValue);
        var user = ApplicationUser.Create(email, fullName, MembershipNumber.FromValue(number), status);

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));

        if (twoFactor)
        {
            await EnrolTwoFactorAsync(user);
        }

        return user;
    }

    private readonly ConcurrentDictionary<Guid, byte[]> _twoFactorKeys = new();
    private readonly ConcurrentDictionary<Guid, long> _lastStepUsed = new();

    /// <summary>Switches two-factor on for an account directly in the database, remembering the key.</summary>
    public async Task EnrolTwoFactorAsync(ApplicationUser user)
    {
        var key = RandomNumberGenerator.GetBytes(Totp.KeyBytes);

        using var scope = Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<AuthenticatorKeyProtector>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Authenticators.Add(new AuthenticatorRecord(user.Id, protector.Protect(key)) { EnabledAt = Clock.GetUtcNow() });
        await db.SaveChangesAsync();

        _twoFactorKeys[user.Id] = key;
    }

    /// <summary>Remembers a key the app generated (for example during enrolment), so codes can be made for it.</summary>
    public void RememberTwoFactorKey(ApplicationUser user, byte[] key) => _twoFactorKeys[user.Id] = key;

    public byte[] TwoFactorKey(ApplicationUser user) => _twoFactorKeys[user.Id];

    /// <summary>The code for a specific time step, with no bookkeeping. For tests of the window itself.</summary>
    public string CodeForStep(ApplicationUser user, long step) => Totp.ComputeCode(_twoFactorKeys[user.Id], step);

    public long CurrentStep => Totp.CurrentStep(Clock.GetUtcNow());

    /// <summary>
    /// A valid code the app has not seen yet. A code is good once, so if the current step was already
    /// used for this account the clock is moved on to the next step first.
    /// </summary>
    public string NextTotpCode(ApplicationUser user)
    {
        var now = Clock.GetUtcNow();
        var step = Totp.CurrentStep(now);

        if (_lastStepUsed.TryGetValue(user.Id, out var last) && step <= last)
        {
            Clock.Advance(DateTimeOffset.FromUnixTimeSeconds((last + 1) * Totp.StepSeconds) - now);
            step = last + 1;
        }

        _lastStepUsed[user.Id] = step;

        return Totp.ComputeCode(_twoFactorKeys[user.Id], step);
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

            services.PostConfigure<TwoFactorOptions>(options => options.AttemptsPerMinutePerUser = TwoFactorAttemptsPerMinute);

            // Test-only endpoints (see ProbeController).
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            services.AddAuthorizationBuilder().AddPolicy(ProbeController.NeverPolicy, policy => policy.RequireAssertion(_ => false));
        });
    }
}
