using CaseRegister.Tests.Architecture;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CaseRegister.Tests.Configuration;

// Not parallel with anything else: one test clears an environment variable.
[Collection(nameof(StartupConfigurationTests))]
public sealed class StartupConfigurationTests : IDisposable
{
    private const string EnvironmentVariable = "ConnectionStrings__Default";
    private readonly string? _originalEnvironmentValue = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public StartupConfigurationTests()
    {
        // A developer machine may have the real connection string in the environment.
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(EnvironmentVariable, _originalEnvironmentValue);

    [Fact]
    public void The_app_fails_at_startup_when_the_default_connection_string_is_missing()
    {
        // The Testing environment does not load user-secrets, so nothing supplies the value.
        using var factory = CreateFactory(settings: null);

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("ConnectionStrings:Default", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_app_fails_at_startup_when_the_default_connection_string_is_blank(string value)
    {
        using var factory = CreateFactory(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = value });

        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
        Assert.Contains("ConnectionStrings:Default", failure.ToString(), StringComparison.Ordinal);
    }

    // Positive control: proves the failures above are about the missing setting and nothing else.
    [Fact]
    public void The_app_starts_when_the_default_connection_string_is_present()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            // Never connected to: the database is only touched when a request needs it.
            ["ConnectionStrings:Default"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
        });

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Set explicitly: the metadata WebApplicationFactory generates for the content root breaks
            // when the project path contains an apostrophe (as a user folder name can).
            builder.UseContentRoot(Path.Combine(SolutionGraph.SolutionRoot, "CaseRegister.Api"));
            builder.UseEnvironment("Testing");
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            }
        });
}
