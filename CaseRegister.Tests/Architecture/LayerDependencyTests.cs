namespace CaseRegister.Tests.Architecture;

public class LayerDependencyTests
{
    private const string Domain = "CaseRegister.Domain";
    private const string Application = "CaseRegister.Application";
    private const string Infrastructure = "CaseRegister.Infrastructure";
    private const string Api = "CaseRegister.Api";

    // Packages and shared frameworks the Domain layer must never depend on.
    private static readonly string[] _forbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Microsoft.AspNetCore",
        "Swashbuckle",
    ];

    [Fact]
    public void Domain_references_no_other_project()
    {
        Assert.Empty(SolutionGraph.ProjectReferences(Domain));
    }

    [Fact]
    public void Domain_references_no_ef_core_or_aspnet_package()
    {
        var offenders = SolutionGraph.Packages(Domain)
            .Concat(SolutionGraph.FrameworkReferences(Domain))
            .Where(name => _forbiddenInDomain.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(offenders.Count == 0, $"Domain must stay free of infrastructure. Found: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData(Infrastructure)]
    [InlineData(Api)]
    public void Application_does_not_reference(string forbiddenProject)
    {
        Assert.DoesNotContain(forbiddenProject, SolutionGraph.ProjectReferences(Application));
    }

    // Guards against the rules above passing vacuously because the graph could not be read.
    [Fact]
    public void Dependency_graph_reflects_the_intended_layering()
    {
        Assert.Contains(Domain, SolutionGraph.ProjectReferences(Application));
        Assert.Contains(Application, SolutionGraph.ProjectReferences(Infrastructure));
        Assert.Contains(Infrastructure, SolutionGraph.ProjectReferences(Api));
        Assert.Contains("Microsoft.AspNetCore.App", SolutionGraph.FrameworkReferences(Api));
    }
}
