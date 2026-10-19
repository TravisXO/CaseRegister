using CaseRegister.Api.Hardening;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auditing;
using CaseRegister.Infrastructure.Auditing;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Certificates;
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

    /// <summary>The hostname passkeys are made for in the tests, and the origin the virtual authenticator reports.</summary>
    public const string PasskeyRelyingPartyId = "app.example.test";
    public const string PasskeyOrigin = "https://app.example.test";
    public const string Password = "correct horse battery staple";
    public const string TestKeyId = "k1";
    public const string SecondKeyId = "k2";

    /// <summary>32 bytes, for tests only.</summary>
    public static readonly string TestKeyBase64 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());

    public const string PseudonymKeyId = "p1";

    public static readonly string PseudonymKeyBase64 = Convert.ToBase64String(Enumerable.Range(51, 32).Select(index => (byte)index).ToArray());

    public static readonly string SecondKeyBase64 = Convert.ToBase64String(Enumerable.Range(101, 32).Select(index => (byte)index).ToArray());

    private readonly Dictionary<string, string?> _settings;
    private readonly string _anchorFilePath;

    /// <summary>The database owner's connection (the one migrations use). Set for factories that have a database; null otherwise.</summary>
    public string? OwnerConnectionString { get; private init; }

    /// <summary>The file this application writes its audit anchors to.</summary>
    public string AnchorFilePath => _anchorFilePath;

    /// <summary>The environment the application starts in. "Testing" unless a test is about another one (never Development: that loads the real secrets).</summary>
    public string EnvironmentName { get; private init; } = "Testing";

    /// <summary>What would have gone to the console, after redaction.</summary>
    public CapturingLogEventSink LogOutput { get; } = new();

    /// <summary>The shared secret of the proxy in tests that run as Production, where the application will not start without one. At least 32 characters.</summary>
    public const string ProxyTestSecret = "proxy-secret-for-tests-0123456789-abcdef";

    public const string NextProxyTestSecret = "next-proxy-secret-for-tests-0123456789-abcd";

    public const string ProxyHeader = "X-Proxy-Secret";

    private ApiFactory(string defaultConnectionString, Action<Dictionary<string, string?>>? adjustSettings, string? anchorFilePath = null, string environmentName = "Testing")
    {
        EnvironmentName = environmentName;
        // The local anchor file is for Development only, so the application is never told about it: the test host is given the store directly.
        _anchorFilePath = anchorFilePath ?? Path.Combine(Path.GetTempPath(), $"cr-test-anchors-{Guid.NewGuid():N}.jsonl");
        _settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = defaultConnectionString,
            ["DataProtection:CertificateBase64"] = TestCertificate.Base64,
            ["DataProtection:CertificatePassword"] = TestCertificate.Password,

            // Cloudflare's published test secret: it accepts every token. The verifier itself is replaced below.
            ["Turnstile:SecretKey"] = TurnstileTestSecret,
            ["Passkeys:RelyingPartyId"] = PasskeyRelyingPartyId,
            ["Passkeys:Origins:0"] = PasskeyOrigin,

            // The only deployment mode there is.
            ["Deployment:Mode"] = "Demo",

            // A neutral test prefix, so no test depends on the real default.
            ["Membership:DisplayPrefix"] = "AGT",

            // Where invite links point. The test host is not a real site; only the shape matters.
            ["Invites:SetupPageUrl"] = "https://app.example.test/staff/setup",

            // The audit chain: a fixed test key.
            ["Audit:CurrentKeyId"] = TestKeyId,
            [$"Audit:Keys:{TestKeyId}"] = TestKeyBase64,

            // The key that pseudonymises email addresses in audit entries.
            ["Pseudonym:CurrentKeyId"] = PseudonymKeyId,
            [$"Pseudonym:Keys:{PseudonymKeyId}"] = PseudonymKeyBase64,
        };

        // Production will not start without the proxy secret and the R2 bucket for the anchors. The bucket is never contacted: the test host is given its own anchor store.
        if (environmentName == "Production")
        {
            _settings["Proxy:Secret"] = ProxyTestSecret;
            _settings["Turnstile:ExpectedHostname"] = "app.example.test";
            _settings["Audit:R2:Endpoint"] = "https://account-for-tests.r2.cloudflarestorage.com";
            _settings["Audit:R2:Bucket"] = "anchors-for-tests";
            _settings["Audit:R2:AccessKeyId"] = "access-key-for-tests";
            _settings["Audit:R2:SecretAccessKey"] = "secret-access-key-for-tests";
        }

        adjustSettings?.Invoke(_settings);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // This app instance is finished: give back its database connections now.
        PostgresFixture.ReleaseConnections(_settings.GetValueOrDefault("ConnectionStrings:Default"));
        TryDeleteAnchors();
        GC.SuppressFinalize(this);
    }

    private void TryDeleteAnchors()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(AnchorFilePath))
            {
                File.Delete(AnchorFilePath);
            }
        }
        catch (IOException)
        {
            // A temporary file; the operating system clears it in time.
        }
    }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public FakeBreachedPasswordChecker Breaches { get; } = new();

    public FakeTurnstileVerifier Turnstile { get; } = new();

    public VerificationCounter PasswordVerifications { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    /// <summary>Per-IP and per-account sign-in limits. High by default so only the rate-limit tests meet them.</summary>
    public int SignInPerIpPerMinute { get; init; } = 10_000;

    public int SignInPerAccountPerHour { get; init; } = 10_000;

    /// <summary>Sign-ups per address per hour, and staff setup calls per address per minute. High by default.</summary>
    public int SignUpPerIpPerHour { get; init; } = 10_000;

    public int StaffSetupPerIpPerMinute { get; init; } = 10_000;

    /// <summary>Extra changes to the application's services, for tests that need to hold the application at a chosen moment.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>How long a request may take. Null keeps the real limit.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>True keeps the anchor store the application chose from its own settings, instead of handing the test host a file or none.</summary>
    public bool RealAnchorSink { get; init; }

    /// <summary>True leaves the rate limits as configured, instead of raising them so tests do not meet them. For tests of the settings themselves.</summary>
    public bool RealLimits { get; init; }

    /// <summary>Two-factor codes tried per user per minute. High by default so only the rate-limit tests meet it.</summary>
    public int TwoFactorAttemptsPerMinute { get; init; } = 10_000;

    /// <summary>Requests of any kind per address per minute. High by default so only the tests of that limit meet it.</summary>
    public int GlobalPerIpPerMinute { get; init; } = 100_000;

    /// <summary>The application with a database behind it: migrated, with the least-privilege role and its grants.</summary>
    public static async Task<ApiFactory> CreateAsync(
        PostgresFixture postgres,
        Action<Dictionary<string, string?>>? adjustSettings = null,
        int signInPerIpPerMinute = 10_000,
        int signInPerAccountPerHour = 10_000,
        int twoFactorAttemptsPerMinute = 10_000,
        int signUpPerIpPerHour = 10_000,
        int staffSetupPerIpPerMinute = 10_000,
        string? anchorFilePath = null,
        string environmentName = "Testing",
        TimeSpan? requestTimeout = null,
        int globalPerIpPerMinute = 100_000,
        Action<IServiceCollection>? configureServices = null)
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();
        await DatabaseMigrator.MigrateAsync(owner);
        await PostgresFixture.ExecuteAsync(owner, await File.ReadAllTextAsync(GrantScriptPath));
        PostgresFixture.ReleaseConnections(owner);

        return new ApiFactory(PostgresFixture.AsAppRole(owner), adjustSettings, anchorFilePath, environmentName)
        {
            RequestTimeout = requestTimeout,
            GlobalPerIpPerMinute = globalPerIpPerMinute,
            ConfigureServices = configureServices,
            SignInPerIpPerMinute = signInPerIpPerMinute,
            SignInPerAccountPerHour = signInPerAccountPerHour,
            TwoFactorAttemptsPerMinute = twoFactorAttemptsPerMinute,
            SignUpPerIpPerHour = signUpPerIpPerHour,
            StaffSetupPerIpPerMinute = staffSetupPerIpPerMinute,
            OwnerConnectionString = owner,
        };
    }

    /// <summary>The application pointed at an empty database that has never been migrated, to show what starting it does and does not do to the schema.</summary>
    public static async Task<ApiFactory> CreateUnmigratedAsync(PostgresFixture postgres, string environmentName)
    {
        await postgres.EnsureAppRoleAsync();
        var owner = await postgres.CreateDatabaseAsync();

        return new ApiFactory(PostgresFixture.AsAppRole(owner), adjustSettings: null, anchorFilePath: null, environmentName) { OwnerConnectionString = owner };
    }

    /// <summary>The application with no database behind it, for tests of configuration only. It never connects.</summary>
    public static ApiFactory WithoutDatabase(Action<Dictionary<string, string?>>? adjustSettings = null, string environmentName = "Testing", bool realLimits = false, TimeSpan? requestTimeout = null, int globalPerIpPerMinute = 100_000, bool realAnchorSink = false) =>
        new("Host=localhost;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1", adjustSettings, environmentName: environmentName) { RealAnchorSink = realAnchorSink, RealLimits = realLimits, RequestTimeout = requestTimeout, GlobalPerIpPerMinute = globalPerIpPerMinute };

    private static string GrantScriptPath => Path.Combine(SolutionGraph.SolutionRoot, "database", "app-role-grants.sql");

    public ApiClient CreateApiClient() => new(this);

    /// <summary>A second, independent instance of the application on the same database, like a second server.</summary>
    public ApiFactory CreateSibling(Action<Dictionary<string, string?>>? adjust = null) => new(_settings["ConnectionStrings:Default"]!, settings =>
    {
        foreach (var (key, value) in _settings)
        {
            settings[key] = value;
        }

        adjust?.Invoke(settings);
    }, _anchorFilePath, EnvironmentName);

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
        bool twoFactor = true,
        Guid? agencyId = null,
        MembershipClass membershipClass = MembershipClass.Full,
        bool staff = false)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Spread numbers out so tests do not collide on the unique membership number.
        var number = membershipNumber != 0 ? membershipNumber : Random.Shared.Next(1000, MembershipNumber.MaxValue);
        var user = staff
            ? ApplicationUser.CreateStaff(email, fullName, status)
            : ApplicationUser.Create(email, fullName, MembershipNumber.FromValue(number), status, membershipClass, agencyId);

        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code)));

        if (twoFactor)
        {
            await EnrolTwoFactorAsync(user);
        }

        return user;
    }


    // --- Agencies, roles and certificates -------------------------------------------------------

    private static int _certificateSequence = 1000;

    public async Task<Agency> CreateAgencyAsync(string name = "Test Agency")
    {
        var agency = Agency.Create(name);

        await WithDbAsync(async db =>
        {
            db.Agencies.Add(agency);
            await db.SaveChangesAsync();

            return 0;
        });

        return agency;
    }

    /// <summary>Changes roles through the real service (so every rule applies) and expects it to be accepted.</summary>
    public async Task AssignRolesAsync(ApplicationUser user, params Role[] roles)
    {
        var result = await TryChangeRolesAsync(new RoleChange(user.Id, roles, []));

        Assert.True(result.Succeeded, string.Join(", ", result.Violations.Select(violation => violation.Code)));
    }

    public async Task<RoleChangeResult> TryChangeRolesAsync(RoleChange change)
    {
        using var scope = Services.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<IRoleAssignmentService>().ChangeRolesAsync(change);
    }

    public async Task<IReadOnlyList<Role>> RolesOfAsync(ApplicationUser user)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return [.. (await users.GetRolesAsync((await users.FindByIdAsync(user.Id.ToString()))!)).Select(Enum.Parse<Role>).Order()];
    }

    /// <summary>Today's date as the certificate checks see it (Zambian time).</summary>
    public DateOnly Today => MembershipCalendar.Today(Clock);

    /// <summary>Adds a practising certificate for a calendar year straight to the database. Verified unless told otherwise.</summary>
    public async Task<PractisingCertificate> AddCertificateAsync(ApplicationUser user, int? year = null, bool verified = true)
    {
        var certificateYear = year ?? Today.Year;
        var number = CertificateNumber.Parse($"{Interlocked.Increment(ref _certificateSequence)}/{certificateYear}");
        var certificate = PractisingCertificate.Create(user.Id, number, new DateOnly(certificateYear, 1, 1), new DateOnly(certificateYear, 12, 31));

        if (verified)
        {
            certificate.Verify(Guid.NewGuid(), Clock.GetUtcNow());
        }

        await WithDbAsync(async db =>
        {
            db.PractisingCertificates.Add(certificate);
            await db.SaveChangesAsync();

            return 0;
        });

        return certificate;
    }

    /// <summary>A member of an agency with the given roles. By default their practising certificate is current and verified.</summary>
    public async Task<ApplicationUser> CreateMemberAsync(
        string email,
        Agency agency,
        Role[] roles,
        bool currentCertificate = true,
        MembershipClass membershipClass = MembershipClass.Full,
        bool twoFactor = true)
    {
        var user = await CreateUserAsync(email, agencyId: agency.Id, membershipClass: membershipClass, twoFactor: twoFactor);
        await AssignRolesAsync(user, roles);

        if (currentCertificate)
        {
            await AddCertificateAsync(user);
        }

        return user;
    }

    /// <summary>A member of the association's staff: no membership number, no agency.</summary>
    public async Task<ApplicationUser> CreateStaffAsync(string email, Role role)
    {
        var user = await CreateUserAsync(email, staff: true);
        await AssignRolesAsync(user, role);

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

    /// <summary>Puts a passkey row in the database for an account, without a ceremony, for tests that only need there to be one.</summary>
    public async Task AddPasskeyRowAsync(ApplicationUser user)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Set<Microsoft.AspNetCore.Identity.IdentityUserPasskey<Guid>>().Add(new()
        {
            UserId = user.Id,
            CredentialId = RandomNumberGenerator.GetBytes(32),
            Data = new Microsoft.AspNetCore.Identity.IdentityPasskeyData { PublicKey = RandomNumberGenerator.GetBytes(77), Name = "Test passkey", CreatedAt = Clock.GetUtcNow(), IsUserVerified = true, Transports = [], AttestationObject = [], ClientDataJson = [] },
        });
        await db.SaveChangesAsync();
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

    /// <summary>The clients the factory hands out come through the proxy, as every real request does: they carry the secret when the application has one. A test of the gate itself builds its own client.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        if (_settings.GetValueOrDefault("Proxy:Secret") is { Length: > 0 } secret)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(ProxyHeader, secret);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Set explicitly: the metadata WebApplicationFactory generates for the content root breaks
        // when the project path contains an apostrophe (as a user folder name can).
        builder.UseContentRoot(Path.Combine(SolutionGraph.SolutionRoot, "CaseRegister.Api"));

        // Not Development: that would load user-secrets and migrate on startup.
        builder.UseEnvironment(EnvironmentName);

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

            if (!RealLimits)
            {
                services.PostConfigure<RateLimitingOptions>(options =>
                {
                options.SignInPerIpPerMinute = SignInPerIpPerMinute;
                options.SignInPerAccountPerHour = SignInPerAccountPerHour;
                options.SignUpPerIpPerHour = SignUpPerIpPerHour;
                options.StaffSetupPerIpPerMinute = StaffSetupPerIpPerMinute;
                options.GlobalPerIpPerMinute = GlobalPerIpPerMinute;
                });

                services.PostConfigure<TwoFactorOptions>(options => options.AttemptsPerMinutePerUser = TwoFactorAttemptsPerMinute);
            }

            if (RequestTimeout is { } timeout)
            {
                services.PostConfigure<Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutOptions>(options =>
                    options.DefaultPolicy = new Microsoft.AspNetCore.Http.Timeouts.RequestTimeoutPolicy { Timeout = timeout, TimeoutStatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status504GatewayTimeout });
            }

            // The local file store is for Development only, so it is handed to the test host directly. A blank path means no store.
            // (A test of which store the application chooses for itself keeps the real registration.)
            if (!RealAnchorSink)
            {
                services.RemoveAll<IAuditAnchorSink>();
                if (string.IsNullOrWhiteSpace(_anchorFilePath))
                {
                    services.AddSingleton<IAuditAnchorSink>(new NoAuditAnchorSink());
                }
                else
                {
                    services.AddSingleton<IAuditAnchorSink>(new FileAuditAnchorSink(_anchorFilePath));
                }
            }

            services.AddSingleton(new LogOutputOverride(LogOutput));

            ConfigureServices?.Invoke(services);

            // Test-only endpoints (see ProbeController).
            services.AddControllers().AddApplicationPart(typeof(ProbeController).Assembly);
            services.AddAuthorizationBuilder().AddPolicy(ProbeController.NeverPolicy, policy => policy.RequireAssertion(_ => false));
        });
    }
}
