using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<AssemblyMarker>(ServiceLifetime.Singleton);

        return services;
    }
}

/// <summary>Points at the Application assembly for scanning.</summary>
public sealed class AssemblyMarker;
