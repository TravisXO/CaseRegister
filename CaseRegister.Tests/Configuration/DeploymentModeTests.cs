using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Infrastructure;
using CaseRegister.Tests.Architecture;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;

namespace CaseRegister.Tests.Configuration;

/// <summary>Demo is the only deployment mode, it has to be set on purpose, and the application will not start as anything else.</summary>
public sealed class DeploymentModeTests
{
    [Fact]
    public void Demo_is_the_only_valid_mode_and_only_exactly_written()
    {
        Assert.True(DeploymentOptions.IsValid("Demo"));

        foreach (var other in new[] { null, "", " ", "demo", "DEMO", "Demo ", "Live", "Production", "Staging", "Real", "Zambia" })
        {
            Assert.False(DeploymentOptions.IsValid(other), $"'{other}'");
        }
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("Production")]
    [InlineData("demo")]
    [InlineData("")]
    [InlineData(" ")]
    public void The_application_will_not_start_in_any_other_mode(string mode)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Deployment:Mode"] = mode);

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("Deployment:Mode", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_application_will_not_start_with_no_mode_at_all_because_there_is_no_default()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove("Deployment:Mode"));

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("Deployment:Mode", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("no default", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_shared_settings_file_gives_a_default_and_each_deployment_sets_it_on_purpose()
    {
        var root = SolutionGraph.SolutionRoot;

        Assert.DoesNotContain("Deployment", File.ReadAllText(Path.Combine(root, "CaseRegister.Api", "appsettings.json")), StringComparison.Ordinal);
        using var development = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "CaseRegister.Api", "appsettings.Development.json")));
        Assert.Equal("Demo", development.RootElement.GetProperty("Deployment").GetProperty("Mode").GetString());
        Assert.Matches(@"key: Deployment__Mode\s+value: Demo", File.ReadAllText(Path.Combine(root, "render.yaml")));
    }

    [Collection(PostgresTestGroup.Name)]
    public sealed class Endpoint(PostgresFixture postgres)
    {
        [Fact]
        public async Task Anyone_can_ask_and_is_told_it_is_a_demo()
        {
            await using var factory = await ApiFactory.CreateAsync(postgres);

            var response = await factory.CreateApiClient().GetAsync("/api/deployment");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["mode"], body.EnumerateObject().Select(property => property.Name));
            Assert.Equal("Demo", body.GetProperty("mode").GetString());
        }
    }
}
