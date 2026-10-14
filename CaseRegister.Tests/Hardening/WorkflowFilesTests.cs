using System.Text.RegularExpressions;
using CaseRegister.Tests.Architecture;

namespace CaseRegister.Tests.Hardening;

/// <summary>What the CI configuration promises, checked as text so a later edit cannot quietly loosen it.</summary>
public sealed partial class WorkflowFilesTests
{
    private static readonly string _root = SolutionGraph.SolutionRoot;

    public static TheoryData<string> Workflows => new() { "ci.yml", "codeql.yml" };

    // Line endings normalised: a Windows checkout may have changed them.
    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([_root, .. path])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Workflow(string name) => Read(".github", "workflows", name);

    /// <summary>The jobs of a workflow: each job's name and its lines, found by indentation (jobs sit two spaces in, under "jobs:").</summary>
    private static List<(string Name, string Text)> Jobs(string workflow)
    {
        var lines = workflow.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(line => line == "jobs:");
        Assert.True(start >= 0, "no jobs: section");

        var jobs = new List<(string, List<string>)>();
        foreach (var line in lines.Skip(start + 1))
        {
            var header = JobHeader().Match(line);
            if (header.Success)
            {
                jobs.Add((header.Groups[1].Value, []));
            }
            else if (jobs.Count > 0)
            {
                jobs[^1].Item2.Add(line);
            }
        }

        return [.. jobs.Select(job => (job.Item1, string.Join('\n', job.Item2)))];
    }

    private static string TopLevel(string workflow)
    {
        var index = workflow.IndexOf("\njobs:", StringComparison.Ordinal);

        return index < 0 ? workflow : workflow[..index];
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_action_is_pinned_to_a_full_commit_with_the_version_in_a_comment(string name)
    {
        var uses = Workflow(name).Split('\n').Where(line => UsesLine().IsMatch(line)).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, line => Assert.Matches(@"uses:\s+[\w./-]+@[0-9a-f]{40}\s+#\s+v\d+(\.\d+)*\s*$", line.TrimEnd()));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_job_has_a_timeout(string name)
    {
        var jobs = Jobs(Workflow(name));

        Assert.NotEmpty(jobs);
        Assert.All(jobs, job => Assert.Matches(@"(?m)^    timeout-minutes:\s+\d+\s*$", job.Text));
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Permissions_are_read_only_by_default(string name)
    {
        var top = TopLevel(Workflow(name));

        Assert.Matches(@"(?m)^permissions:\n  contents: read\s*\n", top);
        Assert.DoesNotMatch(@"(?m)^permissions:\n(  .*\n)*?  \S+: write", top);
    }

    [Fact]
    public void Only_the_codeql_job_may_write_and_only_security_events()
    {
        foreach (var name in new[] { "ci.yml", "codeql.yml" })
        {
            foreach (var (jobName, text) in Jobs(Workflow(name)))
            {
                var writes = Regex.Matches(text, @"^\s+([\w-]+):\s+write\s*$", RegexOptions.Multiline).Select(match => match.Groups[1].Value).ToList();

                if (name == "codeql.yml")
                {
                    Assert.Equal(["security-events"], writes);
                }
                else
                {
                    Assert.True(writes.Count == 0, $"{name} job {jobName} asks for write access");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void The_workflows_need_no_secrets_at_all(string name)
    {
        // Comments may talk about secrets; only what the workflow does counts.
        var text = string.Join('\n', Workflow(name).Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotContain("secrets.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("github.token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GITHUB_TOKEN", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void A_newer_run_on_the_same_branch_cancels_the_older_one(string name)
    {
        var top = TopLevel(Workflow(name));

        Assert.Matches(@"(?m)^concurrency:\n  group: .*github\.ref.*\n  cancel-in-progress: true\s*$", top);
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_checkout_leaves_the_token_in_the_repository(string name)
    {
        var text = Workflow(name);
        var checkouts = Regex.Matches(text, @"uses: actions/checkout@[^\n]*\n(?<with>(?:\s+(?:with:|[\w-]+: [^\n]*)\n?)*)").ToList();

        Assert.NotEmpty(checkouts);
        Assert.All(checkouts, checkout => Assert.Contains("persist-credentials: false", checkout.Groups["with"].Value, StringComparison.Ordinal));
    }

    // --- The main workflow ---------------------------------------------------------------------------------------

    [Fact]
    public void The_main_workflow_runs_on_pushes_and_pull_requests_to_main()
    {
        var top = TopLevel(Workflow("ci.yml"));

        Assert.Matches(@"(?m)^on:\n  push:\n    branches: \[main\]\n  pull_request:\n    branches: \[main\]", top);
    }

    [Fact]
    public void Restores_are_locked_and_the_build_treats_warnings_as_errors()
    {
        var text = Workflow("ci.yml");
        var restores = Regex.Matches(text, @"dotnet restore [^\n]*").Select(match => match.Value).ToList();

        Assert.Equal(2, restores.Count); // the build job and the vulnerability job
        Assert.All(restores, restore => Assert.Contains("--locked-mode", restore, StringComparison.Ordinal));
        Assert.Contains("-warnaserror", text, StringComparison.Ordinal);
        Assert.Matches(@"dotnet build CaseRegister\.slnx[^\n]*--no-restore[^\n]*-warnaserror", text);
        Assert.Matches(@"10\.0\.x", text);
    }

    [Fact]
    public void The_full_test_suite_runs_with_coverage_and_the_report_is_kept()
    {
        var text = Workflow("ci.yml");

        Assert.Contains("dotnet test CaseRegister.slnx", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"--filter", text);
        Assert.Contains("--collect:\"XPlat Code Coverage\"", text, StringComparison.Ordinal);
        Assert.Contains("--settings coverage.runsettings", text, StringComparison.Ordinal);
        Assert.Matches(@"actions/upload-artifact@[0-9a-f]{40}", text);
        Assert.Contains("name: coverage", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?i)codecov|coveralls|sonar", text); // no paid or external service
    }

    [Fact]
    public void Vulnerable_packages_fail_the_build_including_transitive_ones()
    {
        var text = Workflow("ci.yml");

        Assert.Contains("dotnet list CaseRegister.slnx package --vulnerable --include-transitive", text, StringComparison.Ordinal);
        Assert.Contains("has the following vulnerable packages", text, StringComparison.Ordinal);
        Assert.Matches(@"has the following vulnerable packages[^\n]*\n[^\n]*\n\s+exit 1", text);
    }

    [Fact]
    public void The_secret_scan_reads_the_whole_history_with_a_pinned_checked_download()
    {
        var job = Jobs(Workflow("ci.yml")).Single(j => j.Name == "secret-scan").Text;

        Assert.Contains("fetch-depth: 0", job, StringComparison.Ordinal);
        Assert.Matches(@"GITLEAKS_VERSION: ""\d+\.\d+\.\d+""", job);
        Assert.Matches(@"GITLEAKS_SHA256: ""[0-9a-f]{64}""", job);
        Assert.Contains("sha256sum --check", job, StringComparison.Ordinal);
        Assert.Contains("./gitleaks git .", job, StringComparison.Ordinal);
        Assert.Contains("--config .gitleaks.toml", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-git", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--log-opts", job, StringComparison.Ordinal); // no narrowing to some of the commits
    }

    [Fact]
    public void The_image_is_built_but_not_pushed_and_scanned_failing_on_fixable_high_and_critical_issues()
    {
        var job = Jobs(Workflow("ci.yml")).Single(j => j.Name == "docker-image").Text;

        Assert.Contains("docker build", job, StringComparison.Ordinal);
        Assert.DoesNotContain("docker push", job, StringComparison.Ordinal);
        Assert.DoesNotContain("docker login", job, StringComparison.Ordinal);
        Assert.Contains("aquasecurity/trivy-action@", job, StringComparison.Ordinal);
        Assert.Matches(@"severity: HIGH,CRITICAL", job);
        Assert.Matches(@"ignore-unfixed: true", job);
        Assert.Matches(@"exit-code: ""1""", job);
    }

    [Fact]
    public void The_gitleaks_exception_is_narrow_and_the_default_rules_stay_on()
    {
        var config = Read(".gitleaks.toml");

        Assert.Contains("useDefault = true", config, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(config, @"\[\[allowlists\]\]"));
        Assert.Contains("targetRules = [\"generic-api-key\"]", config, StringComparison.Ordinal);
        Assert.Contains("LogRedactionTests", config, StringComparison.Ordinal);
    }

    // --- The front end ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_frontend_job_installs_from_the_lock_file_without_running_install_scripts_and_checks_everything()
    {
        var job = Jobs(Workflow("ci.yml")).Single(j => j.Name == "frontend").Text;

        Assert.Contains("working-directory: frontend", job, StringComparison.Ordinal);
        Assert.Matches(@"actions/setup-node@[0-9a-f]{40}", job);
        Assert.Contains("cache-dependency-path: frontend/package-lock.json", job, StringComparison.Ordinal);
        Assert.Contains("run: npm ci --ignore-scripts", job, StringComparison.Ordinal);
        foreach (var step in new[] { "npm run lint", "npm run typecheck", "npm run test", "npm run build", "npm run check:build", "npm audit --audit-level=high" })
        {
            Assert.Contains($"run: {step}", job, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("--legacy-peer-deps", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", job, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", job, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frontend_job_fails_if_the_generated_types_are_not_what_the_api_description_says()
    {
        var job = Jobs(Workflow("ci.yml")).Single(j => j.Name == "frontend").Text;

        Assert.Contains("npm run api:types", job, StringComparison.Ordinal);
        Assert.Contains("git diff --exit-code -- src/api/schema.d.ts", job, StringComparison.Ordinal);
    }

    [Fact]
    public void The_frontend_has_a_lock_file_and_the_generated_types_are_committed()
    {
        Assert.True(File.Exists(Path.Combine(_root, "frontend", "package-lock.json")));
        Assert.True(File.Exists(Path.Combine(_root, "frontend", "src", "api", "schema.d.ts")));
        Assert.True(File.Exists(Path.Combine(_root, "frontend", "openapi.json")));
    }

    // --- CodeQL --------------------------------------------------------------------------------------------------

    [Fact]
    public void CodeQL_analyses_csharp_on_pushes_pull_requests_and_weekly()
    {
        var text = Workflow("codeql.yml");

        Assert.Matches(@"(?m)^on:\n  push:\n    branches: \[main\]\n  pull_request:\n    branches: \[main\]\n  schedule:\n(    #[^\n]*\n)?    - cron: ""[^""]+""", text);
        Assert.Matches(@"language: \[csharp, javascript-typescript\]", text);
        Assert.Contains("languages: ${{ matrix.language }}", text, StringComparison.Ordinal);
        Assert.Contains("category: \"/language:${{ matrix.language }}\"", text, StringComparison.Ordinal);
        Assert.Matches(@"github/codeql-action/init@[0-9a-f]{40}", text);
        Assert.Matches(@"github/codeql-action/analyze@[0-9a-f]{40}", text);

        // A weekly cron has a single day-of-week and runs once a week.
        var cron = Regex.Match(text, @"cron: ""([^""]+)""").Groups[1].Value.Split(' ');
        Assert.Equal(5, cron.Length);
        Assert.Matches(@"^[0-6]$", cron[4]);
        Assert.Equal("*", cron[2]);
        Assert.Equal("*", cron[3]);
    }

    [Fact]
    public void The_codeql_job_asks_for_security_events_write_and_nothing_else_beyond_reading()
    {
        var job = Jobs(Workflow("codeql.yml")).Single().Text;

        Assert.Matches(@"(?m)^    permissions:\n      security-events: write\n      contents: read\n      actions: read\n", job);
    }

    // --- Dependabot ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nuget")]
    [InlineData("npm")]
    [InlineData("github-actions")]
    [InlineData("docker")]
    public void Dependabot_checks_each_ecosystem_weekly_and_groups_related_updates(string ecosystem)
    {
        var config = Read(".github", "dependabot.yml");
        var starts = Regex.Matches(config, @"(?m)^  - package-ecosystem: ([\w-]+)").ToList();
        var index = starts.FindIndex(match => match.Groups[1].Value == ecosystem);
        Assert.True(index >= 0, $"no {ecosystem} entry");
        var end = index + 1 < starts.Count ? starts[index + 1].Index : config.Length;
        var block = config[starts[index].Index..end];

        Assert.Matches(@"directory: ""/(frontend)?""", block);
        Assert.Contains("interval: weekly", block, StringComparison.Ordinal);
        Assert.Contains("groups:", block, StringComparison.Ordinal);
        Assert.Matches(@"patterns:\n\s+- ""[^""]+""", block);
    }

    [Fact]
    public void Dependabot_has_exactly_those_four_ecosystems()
    {
        var config = Read(".github", "dependabot.yml");

        Assert.Equal(["nuget", "npm", "github-actions", "docker"], Regex.Matches(config, @"(?m)^  - package-ecosystem: ([\w-]+)").Select(match => match.Groups[1].Value));
    }

    // --- Locked restores -----------------------------------------------------------------------------------------

    [Fact]
    public void Lock_files_are_switched_on_and_every_project_has_one()
    {
        Assert.Matches(@"<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", Read("Directory.Build.props"));

        var projects = Directory.GetFiles(_root, "*.csproj", SearchOption.AllDirectories).Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, projects.Count);
        Assert.All(projects, project => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")), $"{project} has no lock file"));
    }

    [Fact]
    public void The_docker_build_restores_in_locked_mode_from_the_same_lock_files()
    {
        var dockerfile = Read("Dockerfile");

        Assert.Contains("dotnet restore CaseRegister.Api/CaseRegister.Api.csproj --locked-mode", dockerfile, StringComparison.Ordinal);
        foreach (var project in new[] { "Domain", "Application", "Infrastructure", "Api" })
        {
            Assert.Contains($"COPY CaseRegister.{project}/CaseRegister.{project}.csproj CaseRegister.{project}/packages.lock.json CaseRegister.{project}/", dockerfile, StringComparison.Ordinal);
        }
    }

    // --- Mutation tests stay local ----------------------------------------------------------------------------------

    [Fact]
    public void Mutation_checks_are_documented_and_are_not_run_by_ci()
    {
        var docs = Read("docs", "testing.md");
        var script = Read("scripts", "mutate.sh");

        Assert.Contains("scripts/mutate.sh", docs, StringComparison.Ordinal);
        Assert.Contains("not in CI", docs, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-timeout 5m", script, StringComparison.Ordinal);
        Assert.Contains("trap", script, StringComparison.Ordinal); // the file is put back when the script ends
        foreach (var name in new[] { "ci.yml", "codeql.yml" })
        {
            Assert.DoesNotContain("mutate", Workflow(name), StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(@"^  ([A-Za-z0-9_-]+):\s*$")]
    private static partial Regex JobHeader();

    [GeneratedRegex(@"^\s*-?\s*uses:\s")]
    private static partial Regex UsesLine();
}
