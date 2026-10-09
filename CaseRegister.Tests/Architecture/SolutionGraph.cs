using System.Text.Json;

namespace CaseRegister.Tests.Architecture;

/// <summary>
/// Reads the restored dependency graph of a project from obj/project.assets.json.
/// Unlike compiled assembly references, this includes transitive references and is not
/// affected by the compiler dropping references that are never used.
/// </summary>
internal static class SolutionGraph
{
    private static readonly string _solutionRoot = FindSolutionRoot();

    /// <summary>Every project the given project depends on, directly or transitively.</summary>
    public static IReadOnlySet<string> ProjectReferences(string project) => Libraries(project, "project");

    /// <summary>Every NuGet package the given project depends on, directly or transitively.</summary>
    public static IReadOnlySet<string> Packages(string project) => Libraries(project, "package");

    /// <summary>Shared framework references (for example Microsoft.AspNetCore.App).</summary>
    public static IReadOnlySet<string> FrameworkReferences(string project)
    {
        using var assets = LoadAssets(project);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (assets.RootElement.TryGetProperty("project", out var projectElement)
            && projectElement.TryGetProperty("frameworks", out var frameworks))
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (framework.Value.TryGetProperty("frameworkReferences", out var references))
                {
                    names.UnionWith(references.EnumerateObject().Select(reference => reference.Name));
                }
            }
        }

        return names;
    }

    private static HashSet<string> Libraries(string project, string type)
    {
        using var assets = LoadAssets(project);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() == type)
            {
                // Library keys look like "Name/1.2.3".
                names.Add(library.Name[..library.Name.IndexOf('/', StringComparison.Ordinal)]);
            }
        }

        return names;
    }

    private static JsonDocument LoadAssets(string project)
    {
        var path = Path.Combine(_solutionRoot, project, "obj", "project.assets.json");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No restored assets for '{project}'. Build the solution before running the architecture tests.",
                path);
        }

        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && directory.GetFiles("*.slnx").Length == 0)
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the solution (.slnx) above the test output folder.");
    }
}
