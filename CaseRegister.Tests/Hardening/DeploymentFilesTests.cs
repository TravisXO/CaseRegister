using System.Text.RegularExpressions;
using CaseRegister.Tests.Architecture;

namespace CaseRegister.Tests.Hardening;

/// <summary>What render.yaml, the deployment scripts and the runbook promise, checked as text: no secret value in the blueprint, every setting documented.</summary>
public sealed partial class DeploymentFilesTests
{
    private static readonly string _root = SolutionGraph.SolutionRoot;

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([_root, .. path]));

    private static string[] BlueprintLines() =>
        [.. Read("render.yaml").Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))];

    /// <summary>Each environment variable in the blueprint, with the other lines that belong to it (its value, or sync: false).</summary>
    private static Dictionary<string, List<string>> EnvironmentVariables()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        var inEnv = false;
        foreach (var line in BlueprintLines())
        {
            if (line.TrimStart().StartsWith("envVars:", StringComparison.Ordinal))
            {
                inEnv = true;
                continue;
            }

            if (!inEnv)
            {
                continue;
            }

            var key = KeyLine().Match(line);
            if (key.Success)
            {
                current = key.Groups[1].Value;
                result[current] = [];
            }
            else if (current is not null)
            {
                result[current].Add(line.Trim());
            }
        }

        return result;
    }

    [GeneratedRegex(@"^\s*-\s+key:\s*(\S+)\s*$")]
    private static partial Regex KeyLine();

    // --- The blueprint ---------------------------------------------------------------------------------------

    [Fact]
    public void It_builds_the_dockerfile_in_the_repository_root_in_frankfurt_on_the_free_plan()
    {
        var lines = BlueprintLines().Select(line => line.Trim()).ToList();

        Assert.Contains("runtime: docker", lines);
        Assert.Contains("dockerfilePath: ./Dockerfile", lines);
        Assert.Contains("dockerContext: .", lines);
        Assert.Contains("region: frankfurt", lines);
        Assert.Contains("plan: free", lines);
        Assert.True(File.Exists(Path.Combine(_root, "Dockerfile")));
    }

    [Fact]
    public void It_checks_the_liveness_address_and_deploys_only_after_the_ci_checks_pass()
    {
        var lines = BlueprintLines().Select(line => line.Trim()).ToList();

        Assert.Contains("healthCheckPath: /health/live", lines);
        Assert.Contains("autoDeployTrigger: checksPass", lines);
        Assert.Contains("branch: main", lines);
    }

    [Fact]
    public void It_runs_as_production_and_that_is_the_only_value_in_it()
    {
        var variables = EnvironmentVariables();

        Assert.Equal(["value: Production"], variables["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal(["value: Demo"], variables["Deployment__Mode"]); // not a secret, and set on purpose: the only mode there is for now
        foreach (var (name, lines) in variables.Where(variable => variable.Key is not ("ASPNETCORE_ENVIRONMENT" or "Deployment__Mode")))
        {
            Assert.Equal(["sync: false"], lines); // asked for when the blueprint is applied, never kept in this file
            Assert.DoesNotContain(lines, line => line.StartsWith("value:", StringComparison.Ordinal) || line.StartsWith("generateValue", StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    [Fact]
    public void Every_setting_the_application_needs_in_production_is_asked_for()
    {
        var names = EnvironmentVariables().Keys;

        string[] required =
        [
            "ConnectionStrings__Default",
            "Audit__Keys__k1",
            "Audit__R2__Endpoint",
            "Audit__R2__Bucket",
            "Audit__R2__AccessKeyId",
            "Audit__R2__SecretAccessKey",
            "DataProtection__CertificateBase64",
            "DataProtection__CertificatePassword",
            "Turnstile__SecretKey",
            "Turnstile__ExpectedHostname",
            "Proxy__Secret",
            "Pseudonym__Keys__p1",
            "Invites__SetupPageUrl",
            "Deployment__Mode",
        ];

        Assert.Equal(required.Order(StringComparer.Ordinal), names.Where(name => name != "ASPNETCORE_ENVIRONMENT").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_migrations_connection_string_is_never_given_to_render()
    {
        // The owner's login stays on the administrator's machine: the running application cannot change the schema.
        Assert.DoesNotContain("Migrations", Read("render.yaml"), StringComparison.Ordinal);
        Assert.DoesNotContain("seed", Read("render.yaml"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_blueprint_contains_nothing_that_looks_like_a_secret()
    {
        var text = Read("render.yaml");

        Assert.DoesNotMatch(@"postgres(ql)?://", text);
        Assert.DoesNotMatch(@"Password=", text);
        Assert.DoesNotMatch(@"[A-Za-z0-9+/]{40,}={0,2}", string.Join('\n', BlueprintLines().Where(line => !line.TrimStart().StartsWith("dockerfilePath", StringComparison.Ordinal))));
    }

    // --- The runbook -------------------------------------------------------------------------------------------

    [Fact]
    public void Every_setting_in_the_blueprint_and_the_settings_to_rotate_are_in_the_runbook_with_how_to_make_them()
    {
        var runbook = Read("docs", "deployment.md");

        foreach (var name in EnvironmentVariables().Keys.Append("Proxy__NextSecret").Append("PROXY_SECRET").Append("API_ORIGIN").Append("PRODUCTION_HOSTNAME").Append("VITE_TURNSTILE_SITE_KEY").Append("ConnectionStrings__Migrations"))
        {
            Assert.Contains(name, runbook, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("New-ProductionKeys.ps1")]
    [InlineData("smoke-test.ps1")]
    [InlineData("app-role-grants.sql")]
    [InlineData("-- migrate")]
    [InlineData("seed-admin")]
    [InlineData("verify-audit")]
    [InlineData("v0.1.0")]
    [InlineData("15 minutes")]
    [InlineData("bucket lock")]
    [InlineData("restore point")]
    [InlineData("Rollback")]
    [InlineData("add first, remove later")]
    [InlineData("Preview deployments")]
    [InlineData("expires")]
    public void The_runbook_covers(string topic)
    {
        Assert.Contains(topic, Read("docs", "deployment.md"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_runbook_puts_the_steps_in_the_order_they_have_to_happen_in()
    {
        var runbook = Read("docs", "deployment.md");
        string[] steps =
        [
            "## 1. Neon",
            "## 2. Keys",
            "## 3. Cloudflare R2",
            "## 4. Cloudflare Pages",
            "## 5. Turnstile",
            "## 6. Render",
            "## 7. Connect",
            "## 8. Migrate",
            "## 9. Grants",
            "## 10. First admins",
            "## 11. Smoke test",
            "## 12. Tag",
        ];

        var positions = steps.Select(step => runbook.IndexOf(step, StringComparison.Ordinal)).ToList();

        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.Order(), positions);
    }

    // --- The scripts ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_key_script_never_writes_a_secret_to_the_screen_and_refuses_a_folder_inside_a_repository()
    {
        var script = Read("scripts", "New-ProductionKeys.ps1");
        var printing = script.Split('\n').Where(line => Regex.IsMatch(line, @"Write-Host|Write-Output|Out-Host")).ToList();

        Assert.NotEmpty(printing);
        Assert.DoesNotContain(printing, line => Regex.IsMatch(line, @"\$password|\$pfx|\$bytes|Get-Content|ToBase64String"));
        Assert.Contains(".git", script, StringComparison.Ordinal);
        Assert.Contains("already has files", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_smoke_test_checks_what_the_brief_asks_for_and_prints_nothing_but_pass_or_fail()
    {
        var script = Read("scripts", "smoke-test.ps1");

        foreach (var check in new[] { "security headers", "robots.txt", "direct request to Render without the secret is refused", "Server header", "liveness" })
        {
            Assert.Contains(check, script, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("[PASS]", script, StringComparison.Ordinal);
        Assert.Contains("[FAIL]", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host $response.Body", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXY_SECRET", script, StringComparison.Ordinal); // it never needs the secret: the point is that it is refused without it
    }
}
