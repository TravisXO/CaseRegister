using CaseRegister.Infrastructure.Seeding;
using Microsoft.Extensions.Hosting;

namespace CaseRegister.Api.Cli;

/// <summary>
/// <c>dotnet run -- seed-demo</c> makes fake agencies and a few pending member applications for trying the flow by hand. Development only:
/// anywhere else it refuses and changes nothing. Running it again adds nothing.
/// </summary>
public static class DemoCommands
{
    public const string Name = "seed-demo";

    public const int Succeeded = 0;
    public const int Refused = 1;
    public const int Misused = 2;

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, ITerminal terminal, CancellationToken cancellationToken = default)
    {
        if (args.Length > 1)
        {
            terminal.WriteLine($"Usage: {Name}");

            return Misused;
        }

        using var scope = services.CreateScope();
        var environment = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
        {
            terminal.WriteLine($"{Name} only runs in the Development environment, not in '{environment.EnvironmentName}'. Nothing was changed.");

            return Refused;
        }

        var result = await scope.ServiceProvider.GetRequiredService<DemoApplicationsSeeder>().SeedAsync(cancellationToken);

        terminal.WriteLine($"Made {result.AgenciesCreated} agencies and {result.ApplicationsCreated} pending applications"
            + (result.AgenciesCreated + result.ApplicationsCreated == 0 ? " (they were all there already)." : "."));

        return Succeeded;
    }
}
