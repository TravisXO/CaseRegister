using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.KeyProtection;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Infrastructure.Seeding;
using CaseRegister.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure;

public static class DependencyInjection
{
    // Nothing here relies on an ASP.NET Core Identity default. Each value is set, and a comment says
    // so wherever it differs from the default.
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TokenLifespan = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromDays(90);
    public const int MaxFailedAccessAttempts = 5;
    public const int PasswordHashIterations = 210_000;
    public const string ApplicationName = "CaseRegister";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EntityTimestampInterceptor>();

        AddDatabase(services);
        AddAgentRegistration(services);
        AddAccountSecurity(services);
        AddKeyProtection(services, configuration);
        AddExternalClients(services);

        services.AddScoped<IAuditLog, DatabaseAuditLog>();
        services.AddScoped<DemoDataSeeder>();

        return services;
    }

    private static void AddDatabase(IServiceCollection services)
    {
        // Read at host start (not at registration) so the final configuration is what gets checked.
        services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Default = configuration[DatabaseOptions.DefaultKey];
                options.Migrations = configuration[DatabaseOptions.MigrationsKey];
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Default),
                $"{DatabaseOptions.DefaultKey} is not configured. Set it with 'dotnet user-secrets' locally "
                + "or the ConnectionStrings__Default environment variable.")
            .ValidateOnStart();

        services.AddDbContext<AppDbContext>((provider, builder) =>
        {
            var connectionString = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Default
                ?? throw new InvalidOperationException($"{DatabaseOptions.DefaultKey} is not configured.");

            AppDbContextOptions.Configure(builder, connectionString);
            builder.AddInterceptors(provider.GetRequiredService<EntityTimestampInterceptor>());
        });
    }

    private static void AddAgentRegistration(IServiceCollection services)
    {
        // The display prefix has no default in code: it comes from configuration and a bad value stops startup.
        services.AddOptions<AgentRegistrationOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.DisplayPrefix = configuration[AgentRegistrationOptions.DisplayPrefixKey] ?? string.Empty)
            .Validate(
                options => AgentRegistrationOptions.IsValidPrefix(options.DisplayPrefix),
                $"{AgentRegistrationOptions.DisplayPrefixKey} must be 1 to {AgentRegistrationOptions.MaxPrefixLength} letters.")
            .ValidateOnStart();
    }

    private static void AddAccountSecurity(IServiceCollection services)
    {
        // These must be registered before AddIdentityCore, which only adds its own defaults when nothing is there.
        services.AddSingleton<ILookupNormalizer, EmailLookupNormalizer>();
        services.AddScoped<IUserConfirmation<ApplicationUser>, ApprovedAccountConfirmation>();

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                // Email is the username, and no two accounts may share one. Default: false.
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

                // NIST SP 800-63B: length, not composition. Default: 6 characters with all four character types required.
                options.Password.RequiredLength = PasswordPolicy.MinimumLength;
                options.Password.RequiredUniqueChars = PasswordPolicy.RequiredUniqueCharacters;
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireNonAlphanumeric = false;

                // Default lockout is 5 minutes. Same attempt count and same "new users can be locked out" as the default.
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;
                options.Lockout.AllowedForNewUsers = true;

                // Only Approved accounts sign in, decided by ApprovedAccountConfirmation. Default: false.
                // There is no email or phone confirmation step.
                options.SignIn.RequireConfirmedAccount = true;
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedPhoneNumber = false;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddUserValidator<AccountUserValidator>()
            .AddPasswordValidator<PasswordLengthLimitValidator>()
            .AddPasswordValidator<PersonalInfoPasswordValidator>()
            .AddPasswordValidator<BreachedPasswordValidator>();

        services.Configure<PasswordHasherOptions>(options =>
        {
            // Default: 100,000. OWASP recommends at least 600,000 for PBKDF2-HMAC-SHA1 and 210,000 for SHA-512, which V3 uses.
            options.IterationCount = PasswordHashIterations;
            options.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
        });

        // Default: 1 day. Password reset and similar tokens are short-lived.
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TokenLifespan);

        services.AddScoped<LockoutTracker>();
        services.AddScoped<DummyPasswordVerifier>();
        services.AddScoped<ISignInService, SignInService>();
        services.AddScoped<IAccountPasswordService, AccountPasswordService>();
    }

    private static void AddKeyProtection(IServiceCollection services, IConfiguration configuration)
    {
        // Throws now, at startup, if the certificate is missing or unreadable.
        var certificate = DataProtectionCertificate.Load(configuration);

        services.AddDataProtection()
            // Default: derived from the content root, which would change between machines and deployments.
            .SetApplicationName(ApplicationName)
            // Default: 90 days. Stated so it cannot silently change.
            .SetDefaultKeyLifetime(KeyLifetime)
            .PersistKeysToDbContext<AppDbContext>()
            .ProtectKeysWithCertificate(certificate);
    }

    private static void AddExternalClients(IServiceCollection services)
    {
        services.AddHttpClient<IBreachedPasswordChecker, HaveIBeenPwnedClient>(client =>
        {
            client.BaseAddress = new Uri(HaveIBeenPwnedClient.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationName);
        });

        services.AddOptions<TurnstileOptions>()
            .Configure<IConfiguration>((options, configuration) => options.SecretKey = configuration[TurnstileOptions.SecretKeyKey])
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SecretKey),
                $"{TurnstileOptions.SecretKeyKey} is not configured. Set it with 'dotnet user-secrets' or the Turnstile__SecretKey environment variable.")
            .ValidateOnStart();

        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(client => client.Timeout = TimeSpan.FromSeconds(5));
    }
}
