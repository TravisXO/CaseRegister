using CaseRegister.Application.Accounts;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Notifications;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.KeyProtection;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Infrastructure.Seeding;
using CaseRegister.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        services.AddSingleton<AfterCommitInterceptor>();

        AddDatabase(services);
        AddMembership(services);
        AddAccountSecurity(services);
        AddKeyProtection(services, configuration);
        AddExternalClients(services);

        AddAudit(services);
        services.AddScoped<DemoDataSeeder>();
        services.AddScoped<DemoApplicationsSeeder>();

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
            builder.AddInterceptors(provider.GetRequiredService<EntityTimestampInterceptor>(), provider.GetRequiredService<AfterCommitInterceptor>());

            // Nothing registers more in the application. Tests do, to make a connection drop at an awkward moment.
            builder.AddInterceptors(provider.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>());
        });
    }

    private static void AddMembership(IServiceCollection services)
    {
        // The display prefix has no default in code: it comes from configuration and a bad value stops startup.
        services.AddOptions<DeploymentOptions>()
            .Configure<IConfiguration>((options, configuration) => options.Mode = configuration[DeploymentOptions.ModeKey])
            .Validate(options => DeploymentOptions.IsValid(options.Mode), DeploymentOptions.Problem)
            .ValidateOnStart();
        services.AddOptions<MembershipOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.DisplayPrefix = configuration[MembershipOptions.DisplayPrefixKey] ?? string.Empty)
            .Validate(
                options => MembershipOptions.IsValidPrefix(options.DisplayPrefix),
                $"{MembershipOptions.DisplayPrefixKey} must be 1 to {MembershipOptions.MaxPrefixLength} letters.")
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

                // Identity's own lockout is switched off: it locked an account for everyone whoever was guessing, which let a stranger shut an
                // admin out. Lockout is per account and address instead (SignInThrottle), and nothing calls AccessFailedAsync.
                options.Lockout.AllowedForNewUsers = false; // Default: true.
                options.Lockout.MaxFailedAccessAttempts = MaxFailedAccessAttempts;
                options.Lockout.DefaultLockoutTimeSpan = LockoutDuration;

                // Only Approved accounts sign in, decided by ApprovedAccountConfirmation. Default: false.
                // There is no email or phone confirmation step.
                options.SignIn.RequireConfirmedAccount = true;
                options.SignIn.RequireConfirmedEmail = false;
                options.SignIn.RequireConfirmedPhoneNumber = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<AppSignInManager>()
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

        services.AddScoped<SignInThrottle>();
        services.AddScoped<IAuditReadLog, AuditReadLog>();
        services.AddScoped<SignInHistory>();
        services.AddScoped<IAccountSecurity>(provider => provider.GetRequiredService<SignInHistory>());
        services.AddScoped<DummyPasswordVerifier>();
        services.AddScoped<ISignInService, SignInService>();
        services.AddScoped<IAccountPasswordService, AccountPasswordService>();

        // Two-factor, sessions and the shared password check.
        services.AddScoped<CredentialChecker>();
        services.AddScoped<ITwoFactorService, TwoFactorService>();
        services.AddScoped<ISessionService, SessionService>();

        // Roles, certificates and the member profile.
        services.AddScoped<RoleAssignmentService>();
        services.AddScoped<IRoleAssignmentService>(provider => provider.GetRequiredService<RoleAssignmentService>());
        AddAdminApprovals(services);
        services.AddScoped<IPractisingCertificateService, PractisingCertificateService>();
        services.AddScoped<IMemberProfileService, MemberProfileService>();
        services.AddSingleton<AuthenticatorKeyProtector>();
        services.AddSingleton<IValidateOptions<TwoFactorOptions>, TwoFactorOptionsValidator>();
        services.AddOptions<TwoFactorOptions>().BindConfiguration(TwoFactorOptions.SectionName).ValidateOnStart();
        services.AddSingleton<TwoFactorAttemptLimiter>();

        // Sessions live in Postgres. The cookie handler is pointed at this store by the Api project.
        services.AddHttpContextAccessor();
        services.AddSingleton<IClientLocationAccessor, ClientLocationAccessor>();
        services.AddSingleton<ITicketStore, DatabaseTicketStore>();
    }

    private static void AddAudit(IServiceCollection services)
    {
        // The keys come from configuration (user-secrets, then environment variables) and are never in code. Startup fails without them.
        services.AddSingleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>();
        services.AddOptions<AuditOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var section = configuration.GetSection(AuditOptions.SectionName);
                options.CurrentKeyId = section["CurrentKeyId"] ?? string.Empty;
                options.AnchorFilePath = section["AnchorFilePath"];
                section.GetSection("R2").Bind(options.R2);
                foreach (var key in section.GetSection("Keys").GetChildren())
                {
                    options.Keys[key.Key] = key.Value ?? string.Empty;
                }
            })
            .ValidateOnStart();

        services.AddSingleton<IAuditKeyring, AuditKeyring>();

        // The separate key that pseudonymises email addresses in audit entries. Like the audit keys it comes from configuration only, and startup fails without it.
        services.AddSingleton<IValidateOptions<PseudonymOptions>, PseudonymOptionsValidator>();
        services.AddOptions<PseudonymOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var section = configuration.GetSection(PseudonymOptions.SectionName);
                options.CurrentKeyId = section["CurrentKeyId"] ?? string.Empty;
                foreach (var key in section.GetSection("Keys").GetChildren())
                {
                    options.Keys[key.Key] = key.Value ?? string.Empty;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IPseudonymiser, Pseudonymiser>();

        // Where daily anchors go. Production has a Cloudflare R2 bucket (and refuses to start without one); Development may use a local file
        // or nothing. There is no scheduler: the first audit entry written on a new Zambian day writes that day's anchor (see ChainedAuditLog).
        services.AddHttpClient(R2AuditAnchorSink.ClientName, client => client.Timeout = ExternalCalls.ClientTimeout).AddExternalCallResilience();
        services.AddSingleton<IAuditAnchorSink>(provider =>
        {
            var configured = provider.GetRequiredService<IOptions<AuditOptions>>().Value;
            if (configured.R2.AnySet)
            {
                return new R2AuditAnchorSink(provider.GetRequiredService<IHttpClientFactory>(), provider.GetRequiredService<IOptions<AuditOptions>>(), provider.GetRequiredService<TimeProvider>());
            }

            var path = configured.AnchorFilePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return new NoAuditAnchorSink();
            }

            var root = provider.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().ContentRootPath;

            return new FileAuditAnchorSink(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        });

        services.AddScoped<AuditChainStore>();
        services.AddScoped<IAuditLog, ChainedAuditLog>();
        services.AddScoped<IAuditAnchoring, AuditAnchoring>();
        services.AddScoped<AuditAnchorPublisher>();
        services.AddScoped<AuditAnchorGate>();
        services.AddScoped<IAuditVerifier, AuditVerifier>();
        services.AddScoped<IAuditReader, AuditReader>();
    }

    private static void AddAdminApprovals(IServiceCollection services)
    {
        // One operation per kind of admin action. The engine and the immediate actions find them by type.
        services.AddScoped<IAdminOperation>(provider => new RoleChangeOperation(PendingActionType.AssignRole, provider.GetRequiredService<RoleAssignmentService>(), provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<UserManager<ApplicationUser>>()));
        services.AddScoped<IAdminOperation>(provider => new RoleChangeOperation(PendingActionType.RemoveRole, provider.GetRequiredService<RoleAssignmentService>(), provider.GetRequiredService<AppDbContext>(), provider.GetRequiredService<UserManager<ApplicationUser>>()));
        services.AddScoped<IAdminOperation, VerifyCertificateOperation>();
        services.AddScoped<IAdminOperation, ReactivateUserOperation>();
        services.AddScoped<IAdminOperation, ReactivateAgencyOperation>();
        services.AddScoped<IAdminOperation, ResetTwoFactorOperation>();
        services.AddScoped<IAdminOperation, SuspendUserOperation>();
        services.AddScoped<IAdminOperation, EndSessionsOperation>();
        services.AddScoped<IAdminOperation, SuspendAgencyOperation>();

        services.AddScoped<IAdminOperation, ApproveApplicationOperation>();
        services.AddScoped<IAdminOperation, RejectApplicationOperation>();
        services.AddScoped<IAdminOperation, InviteStaffOperation>();

        // Sign-up, review, and the last step of a staff invite.
        services.AddScoped<IMemberSignUpService, MemberSignUpService>();
        services.AddScoped<IApplicationReviewService, ApplicationReviewService>();
        services.AddScoped<IStaffSetupService, StaffSetupService>();
        services.AddSingleton<SetupFailureTracker>();

        // Where the setup link points. No default in code: it comes from configuration and a bad value stops startup.
        services.AddOptions<StaffInviteOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.SetupPageUrl = configuration[StaffInviteOptions.SetupPageUrlKey] ?? string.Empty)
            .Validate(
                options => StaffInviteOptions.IsValid(options.SetupPageUrl),
                $"{StaffInviteOptions.SetupPageUrlKey} must be an absolute https address (http only for localhost) with no # in it.")
            .ValidateOnStart();

        AddNotifications(services);

        services.AddScoped<IPendingActionService, PendingActionService>();
        services.AddScoped<IAccountAdministrationService, AccountAdministrationService>();
        services.AddScoped<IAgencyStatus, AgencyStatusReader>();
        services.AddScoped<IAdminSeeder, AdminSeeder>();
        services.AddScoped<IBreakGlassService, BreakGlassService>();
        services.AddScoped<IAdminDirectory, AdminDirectory>();
    }

    private static void AddNotifications(IServiceCollection services)
    {
        services.AddScoped<NotificationRecipients>();
        services.AddScoped<TimeBasedNotifications>();
        services.AddScoped<NotificationDispatcher>();
        services.AddScoped<INotificationService, NotificationService>();
    }

    private static void AddKeyProtection(IServiceCollection services, IConfiguration configuration)
    {
        // Throws now, at startup, if the certificate is missing or unreadable. Held for as long as the application runs.
        var certificates = DataProtectionCertificates.Load(configuration);
        services.AddSingleton(certificates);
        services.AddHostedService<KeyRingGuard>();
        services.AddScoped<KeyVerifier>();

        services.AddDataProtection()
            // Default: derived from the content root, which would change between machines and deployments.
            .SetApplicationName(ApplicationName)
            // Default: 90 days. Stated so it cannot silently change.
            .SetDefaultKeyLifetime(KeyLifetime)
            // Default: AES-256-CBC with an HMAC. GCM is authenticated encryption in one step, and is what the
            // rest of the system uses for data at rest.
            .UseCryptographicAlgorithms(new AuthenticatedEncryptorConfiguration
            {
                EncryptionAlgorithm = EncryptionAlgorithm.AES_256_GCM,
                ValidationAlgorithm = ValidationAlgorithm.HMACSHA256,
            })
            .PersistKeysToDbContext<AppDbContext>()
            // New keys are protected with the current certificate. Older certificates are listed only so that keys protected with them can still be read.
            .ProtectKeysWithCertificate(certificates.Current)
            .UnprotectKeysWithAnyCertificate([.. certificates.All]);
    }

    internal static void AddExternalClients(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ExternalCallOptions>, ExternalCallOptionsValidator>();
        services.AddOptions<ExternalCallOptions>().BindConfiguration(ExternalCallOptions.SectionName).ValidateOnStart();

        services.AddHttpClient<HaveIBeenPwnedClient>(client =>
        {
            client.BaseAddress = new Uri(HaveIBeenPwnedClient.BaseAddress);
            client.Timeout = ExternalCalls.ClientTimeout;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ApplicationName);
        }).AddExternalCallResilience();

        // Have I Been Pwned first; the bundled list, with a note in the audit log, only if it cannot be reached.
        services.AddScoped<IBreachedPasswordChecker, ResilientBreachedPasswordChecker>();

        services.AddOptions<TurnstileOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.SecretKey = configuration[TurnstileOptions.SecretKeyKey];
                options.ExpectedHostname = configuration[TurnstileOptions.ExpectedHostnameKey];
                options.RequireAction = !bool.TryParse(configuration[TurnstileOptions.RequireActionKey], out var require) || require;
            })
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.SecretKey),
                $"{TurnstileOptions.SecretKeyKey} is not configured. Set it with 'dotnet user-secrets' or the Turnstile__SecretKey environment variable.")
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<TurnstileOptions>, TurnstileOptionsValidator>();
        services.AddHttpClient<ITurnstileVerifier, TurnstileVerifier>(client => client.Timeout = ExternalCalls.ClientTimeout).AddExternalCallResilience();
    }
}

/// <summary>Stops startup, with the reason, if the audit keys are not usable.</summary>
/// <summary>Production must say which site the checks are for and must check the form's action: without both, a token earned elsewhere would do.</summary>
internal sealed class TurnstileOptionsValidator(Microsoft.Extensions.Hosting.IHostEnvironment environment) : IValidateOptions<TurnstileOptions>
{
    public ValidateOptionsResult Validate(string? name, TurnstileOptions options)
    {
        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedHostname))
        {
            return ValidateOptionsResult.Fail($"{TurnstileOptions.ExpectedHostnameKey} is not configured. Set the Turnstile__ExpectedHostname environment variable to the hostname of the site (the Pages hostname), so a token earned on another site is refused.");
        }

        return options.RequireAction
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"{TurnstileOptions.RequireActionKey} must not be turned off in Production: it is what stops a token from one form being used on another.");
    }
}

internal sealed class PseudonymOptionsValidator : IValidateOptions<PseudonymOptions>
{
    public ValidateOptionsResult Validate(string? name, PseudonymOptions options) =>
        options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
}

internal sealed class AuditOptionsValidator(Microsoft.Extensions.Hosting.IHostEnvironment environment) : IValidateOptions<AuditOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        if (options.Problem() is { } problem)
        {
            return ValidateOptionsResult.Fail(problem);
        }

        // The anchors are there so someone with only the database cannot rewrite history unnoticed. A file on the same machine does not do that.
        if (!environment.IsDevelopment() && !string.IsNullOrWhiteSpace(options.AnchorFilePath))
        {
            return ValidateOptionsResult.Fail("Audit:AnchorFilePath is the local file anchor store, which is for Development only. Remove it and configure the R2 bucket (Audit:R2:*).");
        }

        // Production must have the bucket. Anywhere else it is optional, but if it is started it has to be complete.
        return (environment.IsProduction() || options.R2.AnySet) && options.R2.Problem() is { } bucketProblem
            ? ValidateOptionsResult.Fail(bucketProblem)
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Stops startup, naming the setting and never its value, if the two-factor limit is not a positive number.</summary>
internal sealed class TwoFactorOptionsValidator : IValidateOptions<TwoFactorOptions>
{
    public ValidateOptionsResult Validate(string? name, TwoFactorOptions options) =>
        options.AttemptsPerMinutePerUser < 1
            ? ValidateOptionsResult.Fail($"{TwoFactorOptions.SectionName}:AttemptsPerMinutePerUser must be at least 1.")
            : ValidateOptionsResult.Success;
}
