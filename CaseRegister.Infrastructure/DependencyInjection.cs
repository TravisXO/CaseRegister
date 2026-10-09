using CaseRegister.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EntityTimestampInterceptor>();

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

        return services;
    }
}
