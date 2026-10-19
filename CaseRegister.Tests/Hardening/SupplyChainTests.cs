using System.Text.RegularExpressions;
using CaseRegister.Tests.Architecture;

namespace CaseRegister.Tests.Hardening;

/// <summary>What the supply-chain files promise (pinned images, the security policy, Scorecard, the bills of materials, the release), checked as text.</summary>
public sealed partial class SupplyChainTests
{
    private static readonly string _root = SolutionGraph.SolutionRoot;

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([_root, .. path])).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Workflow(string name) => Read(".github", "workflows", name);

    private static readonly string[] _buildOutput = ["node_modules", "bin", "obj"];

    private static bool IsBuildOutput(string path) =>
        _buildOutput.Any(folder => path.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The image of every FROM in every Dockerfile that is not "scratch" or the name of an earlier stage.</summary>
    private static IEnumerable<(string File, string Image)> DockerfileImages()
    {
        foreach (var file in Directory.GetFiles(_root, "Dockerfile*", SearchOption.AllDirectories).Where(path => !IsBuildOutput(path)))
        {
            var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(file).Where(line => line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var image = parts.Skip(1).First(part => !part.StartsWith("--", StringComparison.Ordinal));
                if (!image.Equals("scratch", StringComparison.OrdinalIgnoreCase) && !stages.Contains(image))
                {
                    yield return (Path.GetRelativePath(_root, file), image);
                }

                if (parts.Length >= 2 && parts[^2].Equals("AS", StringComparison.OrdinalIgnoreCase))
                {
                    stages.Add(parts[^1]);
                }
            }
        }
    }

    /// <summary>The images named in workflows: an "image:" setting or an environment value called ...IMAGE.</summary>
    private static IEnumerable<(string File, string Image)> WorkflowImages() =>
        Directory.GetFiles(Path.Combine(_root, ".github", "workflows"), "*.yml").SelectMany(file =>
            ImageSetting().Matches(File.ReadAllText(file)).Select(match => (Path.GetFileName(file), match.Groups["image"].Value.Trim('"'))));

    [Fact]
    public void Every_dockerfile_image_is_pinned_by_digest()
    {
        var images = DockerfileImages().ToList();

        Assert.True(images.Count >= 3, "the API's two images and the workflows' PostgreSQL image");
        Assert.All(images, item => Assert.Matches(@"@sha256:[0-9a-f]{64}$", item.Image));
    }

    [Fact]
    public void Every_image_a_workflow_names_is_pinned_by_digest()
    {
        var images = WorkflowImages().ToList();

        Assert.NotEmpty(images);
        Assert.All(images, item => Assert.Matches(@"@sha256:[0-9a-f]{64}$", item.Image));
    }

    [Fact]
    public void The_postgres_image_the_workflows_use_is_the_one_dependabot_watches()
    {
        var watched = Regex.Match(Read(".github", "images", "Dockerfile"), @"FROM (postgres:\d+@sha256:[0-9a-f]{64})").Groups[1].Value;

        Assert.NotEmpty(watched);
        foreach (var name in new[] { "backup.yml", "restore-check.yml" })
        {
            var images = ImageSetting().Matches(Workflow(name)).Select(match => match.Groups["image"].Value.Trim('"')).ToList();
            Assert.NotEmpty(images);
            Assert.All(images, image => Assert.Equal(watched, image)); // when Dependabot moves its line, these move with it
        }
    }

    [Fact]
    public void Ci_fails_the_build_if_an_image_is_not_pinned_and_the_check_looks_everywhere()
    {
        var ci = Workflow("ci.yml");
        var script = Read("scripts", "check-image-pins.sh");

        Assert.Matches(@"image-pins:\n    name: [^\n]+\n    runs-on: ubuntu-latest\n    timeout-minutes: \d+", ci);
        Assert.Contains("run: bash scripts/check-image-pins.sh", ci, StringComparison.Ordinal);
        Assert.Contains("@sha256:", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("find . -name 'Dockerfile*'", script, StringComparison.Ordinal);
        Assert.Contains(".github/workflows", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Dependabot_watches_the_dockerfile_and_the_image_file()
    {
        var config = Read(".github", "dependabot.yml");

        Assert.Matches(@"package-ecosystem: docker\n    directory: ""/""", config);
        Assert.Matches(@"package-ecosystem: docker\n    directory: ""/\.github/images""", config);
    }

    // --- The security policy ------------------------------------------------------------------------------------------

    [Fact]
    public void The_security_policy_says_how_to_report_privately_and_sets_a_ninety_day_disclosure()
    {
        var policy = Read("SECURITY.md");

        Assert.Contains("Report a vulnerability", policy, StringComparison.Ordinal);
        Assert.Contains("do not open a public issue", policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**90 days**", policy, StringComparison.Ordinal);
        Assert.Contains("Safe harbour", policy, StringComparison.Ordinal);
        Assert.Contains("working days", policy, StringComparison.Ordinal);
    }

    // --- Scorecard -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Scorecard_runs_weekly_and_on_pushes_to_main_pinned_and_sends_its_results_to_code_scanning()
    {
        var text = Workflow("scorecard.yml");

        Assert.Matches(@"(?m)^on:\n  schedule:\n    - cron: ""[^""]+""\n  push:\n    branches: \[main\]", text);
        Assert.Matches(@"ossf/scorecard-action@[0-9a-f]{40} # v\d+\.\d+\.\d+", text);
        Assert.Matches(@"github/codeql-action/upload-sarif@[0-9a-f]{40} # v\d+", text);
        Assert.Contains("results_format: sarif", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^permissions:\n  contents: read\s*\n", text);
        Assert.Matches(@"(?m)^    timeout-minutes:\s+\d+", text);
        Assert.Matches(@"uses: actions/checkout@[^\n]*\n\s+with:\n\s+persist-credentials: false", text);

        // The job asks for what Scorecard documents, and no more.
        var job = text[text.IndexOf("    permissions:", StringComparison.Ordinal)..text.IndexOf("    steps:", StringComparison.Ordinal)];
        Assert.Equal(["actions", "contents", "id-token", "security-events"], Regex.Matches(job, @"^      ([\w-]+):", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
    }

    // --- The bills of materials and the release ------------------------------------------------------------------------------

    [Fact]
    public void Ci_makes_both_bills_of_materials_and_keeps_them()
    {
        var ci = Workflow("ci.yml");
        var script = Read("scripts", "make-sboms.sh");
        var tools = Read("dotnet-tools.json");

        Assert.Matches(@"sbom:\n    name: [^\n]+\n    runs-on: ubuntu-latest\n    timeout-minutes: \d+", ci);
        Assert.Contains("run: bash scripts/make-sboms.sh sbom", ci, StringComparison.Ordinal);
        Assert.Contains("name: sbom", ci, StringComparison.Ordinal);
        Assert.Contains("\"cyclonedx\"", tools, StringComparison.Ordinal);
        Assert.Matches(@"@cyclonedx/cyclonedx-npm@\d+\.\d+\.\d+", script); // a fixed version
        Assert.Contains("caseregister-api.cdx.json", script, StringComparison.Ordinal);
        Assert.Contains("caseregister-frontend.cdx.json", script, StringComparison.Ordinal);
        Assert.Contains("--omit dev", script, StringComparison.Ordinal); // what the site ships, not the tools that build it
    }

    [Fact]
    public void A_release_tag_makes_both_bills_attests_each_and_attaches_them()
    {
        var text = Workflow("release.yml");

        Assert.Matches(@"(?m)^on:\n  push:\n    tags: \[""v\*""\]", text);
        Assert.Matches(@"(?m)^permissions:\n  contents: read\s*\n", text);
        Assert.Matches(@"(?m)^    timeout-minutes:\s+\d+", text);
        Assert.Equal(2, Regex.Count(text, @"actions/attest-build-provenance@[0-9a-f]{40} # v\d+\.\d+\.\d+"));
        Assert.Contains("subject-path: sbom/caseregister-api.cdx.json", text, StringComparison.Ordinal);
        Assert.Contains("subject-path: sbom/caseregister-frontend.cdx.json", text, StringComparison.Ordinal);
        Assert.Contains("gh release create \"$TAG\" sbom/caseregister-api.cdx.json sbom/caseregister-frontend.cdx.json", text, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", text, StringComparison.Ordinal);

        // What the one job may do, and nothing else: create the release, sign attestations, store them.
        var job = text[text.IndexOf("    permissions:", StringComparison.Ordinal)..text.IndexOf("    steps:", StringComparison.Ordinal)];
        Assert.Equal(["attestations", "contents", "id-token"], Regex.Matches(job, @"^      ([\w-]+):", RegexOptions.Multiline).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_release_workflow_never_puts_the_tag_name_into_a_command()
    {
        var code = string.Join('\n', Workflow("release.yml").Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

        Assert.DoesNotMatch(@"run:[^\n]*\$\{\{ *github\.(ref_name|head_ref|event)", code);
        Assert.Contains("TAG: ${{ github.ref_name }}", code, StringComparison.Ordinal); // through the environment
    }

    [Theory]
    [InlineData("scorecard.yml")]
    [InlineData("release.yml")]
    public void Every_action_is_pinned_to_a_full_commit_with_the_version_in_a_comment(string name)
    {
        var uses = Workflow(name).Split('\n').Where(line => UsesLine().IsMatch(line)).ToList();

        Assert.NotEmpty(uses);
        Assert.All(uses, line => Assert.Matches(@"uses:\s+[\w./-]+@[0-9a-f]{40}\s+#\s+v\d+(\.\d+)*\s*$", line.TrimEnd()));
    }

    [GeneratedRegex(@"(?m)^\s*(?:[A-Za-z_]*_IMAGE|image):\s*(?<image>[^\s#]+)")]
    private static partial Regex ImageSetting();

    [GeneratedRegex(@"^\s*-?\s*uses:\s")]
    private static partial Regex UsesLine();
}
