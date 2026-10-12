using System.Text.RegularExpressions;
using CaseRegister.Tests.Architecture;

namespace CaseRegister.Tests.Hardening;

/// <summary>What the Dockerfile and .dockerignore promise, checked as text. (The image itself is built and run by hand; see the commit notes.)</summary>
public sealed partial class ContainerFilesTests
{
    private static readonly string _root = SolutionGraph.SolutionRoot;

    private static string[] DockerfileLines() =>
        [.. File.ReadAllLines(Path.Combine(_root, "Dockerfile")).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];

    private static string Dockerfile() => string.Join('\n', DockerfileLines());

    private static string[] IgnoreEntries() =>
        [.. File.ReadAllLines(Path.Combine(_root, ".dockerignore")).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))];

    [Fact]
    public void The_dockerfile_is_where_the_platform_looks_for_it_and_the_old_one_is_gone()
    {
        Assert.True(File.Exists(Path.Combine(_root, "Dockerfile")));
        Assert.False(File.Exists(Path.Combine(_root, "CaseRegister.Api", "Dockerfile")));
    }

    [Fact]
    public void It_builds_in_one_stage_and_runs_in_another()
    {
        var froms = DockerfileLines().Where(line => line.StartsWith("FROM ", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, froms.Count);
        Assert.Contains("mcr.microsoft.com/dotnet/sdk:10.0", froms[0], StringComparison.Ordinal);
        Assert.EndsWith("AS build", froms[0], StringComparison.Ordinal);
    }

    [Fact]
    public void It_runs_on_the_chiseled_dotnet_10_aspnet_image()
    {
        var final = DockerfileLines().Last(line => line.StartsWith("FROM ", StringComparison.Ordinal));

        Assert.StartsWith("FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled", final, StringComparison.Ordinal);
    }

    [Fact]
    public void It_does_not_run_as_root()
    {
        var lines = DockerfileLines();
        var userIndex = Array.FindIndex(lines, line => line.StartsWith("USER ", StringComparison.Ordinal));

        Assert.True(userIndex > Array.FindLastIndex(lines, line => line.StartsWith("FROM ", StringComparison.Ordinal)), "USER is set in the final stage");
        Assert.Equal("USER $APP_UID", lines[userIndex]);
        Assert.DoesNotContain(lines, line => Regex.IsMatch(line, @"^USER\s+(root|0)\b", RegexOptions.IgnoreCase));
        Assert.True(userIndex < Array.FindIndex(lines, line => line.StartsWith("ENTRYPOINT", StringComparison.Ordinal)));
    }

    [Fact]
    public void It_starts_in_production_and_the_diagnostics_channel_that_writes_a_file_is_off()
    {
        var text = Dockerfile();

        Assert.Matches(@"ASPNETCORE_ENVIRONMENT=Production", text);
        Assert.Matches(@"DOTNET_EnableDiagnostics=0", text);
    }

    [Fact]
    public void It_checks_its_own_health_on_live_with_the_app_because_the_image_has_no_shell_or_curl()
    {
        var text = Dockerfile();

        Assert.Contains("HEALTHCHECK", text, StringComparison.Ordinal);
        Assert.Contains("CMD [\"dotnet\", \"CaseRegister.Api.dll\", \"healthcheck\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wget", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/health/ready", text, StringComparison.Ordinal); // the check must never wake the database
    }

    [Fact]
    public void It_starts_the_app_and_nothing_else()
    {
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"CaseRegister.Api.dll\"]", Dockerfile(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_final_stage_takes_only_the_published_output_from_the_build()
    {
        var lines = DockerfileLines();
        var finalStart = Array.FindLastIndex(lines, line => line.StartsWith("FROM ", StringComparison.Ordinal));
        var copies = lines.Skip(finalStart).Where(line => line.StartsWith("COPY ", StringComparison.Ordinal)).ToList();

        Assert.Equal(["COPY --from=build /app ."], copies);
    }

    [Fact]
    public void The_dockerfile_sets_no_setting_that_could_hold_a_secret_and_contains_no_credentials()
    {
        var text = Dockerfile();

        Assert.DoesNotMatch(@"(?im)^\s*(ENV|ARG)\s+[^\n]*(PASSWORD|SECRET|TOKEN|KEY|CONNECTIONSTRING|CERTIFICATE|ConnectionStrings)", text);
        Assert.DoesNotMatch(CredentialLiteral(), text);
    }

    [Fact]
    public void It_does_not_copy_the_whole_context_in_at_once()
    {
        Assert.DoesNotMatch(@"(?m)^COPY\s+(--\S+\s+)*\.\s+\.?/?\S*$", Dockerfile());
    }

    [Theory]
    [InlineData("**/bin/")]
    [InlineData("**/obj/")]
    [InlineData(".git/")]
    [InlineData("CaseRegister.Tests/")]
    [InlineData("CLAUDE.md")]
    [InlineData(".claude/")]
    [InlineData("**/.env")]
    [InlineData("**/secrets.json")]
    [InlineData("**/*.pfx")]
    [InlineData("**/*.pem")]
    [InlineData("**/*.key")]
    [InlineData("**/appsettings.Development.json")]
    [InlineData("**/App_Data/")]
    public void The_docker_ignore_file_keeps_this_out_of_the_build_context(string entry)
    {
        Assert.Contains(entry, IgnoreEntries());
    }

    [Fact]
    public void The_docker_ignore_file_does_not_hide_what_the_build_needs()
    {
        var entries = IgnoreEntries();

        foreach (var needed in new[] { "CaseRegister.Api", "CaseRegister.Application", "CaseRegister.Domain", "CaseRegister.Infrastructure", "Directory.Build.props", "Directory.Packages.props", ".editorconfig" })
        {
            Assert.DoesNotContain(entries, entry => entry.TrimEnd('/') == needed);
        }

        // The Dockerfile copies these four projects, plus the shared build files.
        var copied = DockerfileLines().Where(line => line.StartsWith("COPY ", StringComparison.Ordinal)).ToList();
        foreach (var project in new[] { "CaseRegister.Domain", "CaseRegister.Application", "CaseRegister.Infrastructure", "CaseRegister.Api" })
        {
            Assert.Contains(copied, line => line.Contains($"{project}/", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(copied, line => line.Contains("CaseRegister.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void The_visual_studio_container_tooling_is_gone_because_it_pointed_at_the_old_dockerfile()
    {
        var launch = File.ReadAllText(Path.Combine(_root, "CaseRegister.Api", "Properties", "launchSettings.json"));
        var project = File.ReadAllText(Path.Combine(_root, "CaseRegister.Api", "CaseRegister.Api.csproj"));

        Assert.DoesNotContain("Docker", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("Containers.Tools", project, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"password\s+'|postgres(ql)?://\S+:\S+@", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLiteral();
}
