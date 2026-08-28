using System.Xml.Linq;
using Xunit;

namespace Vistara.ArchitectureTests;

public sealed class ArchitectureRulesTests
{
    [Fact]
    public void Production_project_graph_matches_the_approved_dependency_rules()
    {
        string repositoryRoot = RepositoryLayout.FindRoot();
        IReadOnlyList<ProjectNode> projects = ProjectGraphLoader.Load(repositoryRoot);

        IReadOnlyList<string> violations = ProjectGraphPolicy.Validate(projects);

        Assert.True(
            violations.Count == 0,
            $"Architecture violations:{Environment.NewLine}- "
            + string.Join($"{Environment.NewLine}- ", violations));
    }

    [Fact]
    public void Web_and_backend_source_trees_are_isolated()
    {
        string repositoryRoot = RepositoryLayout.FindRoot();
        IReadOnlyList<string> violations = SourceTreePolicy.Validate(repositoryRoot);

        Assert.True(
            violations.Count == 0,
            $"Source-boundary violations:{Environment.NewLine}- "
            + string.Join($"{Environment.NewLine}- ", violations));
    }
}

internal static class SourceTreePolicy
{
    private static readonly HashSet<string> FrontendSourceExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jsx",
            ".svelte",
            ".ts",
            ".tsx",
            ".vue",
        };

    internal static IReadOnlyList<string> Validate(string repositoryRoot)
    {
        string sourceRoot = Path.Combine(repositoryRoot, "src");
        string webRoot = Path.Combine(sourceRoot, "Vistara.Web");
        if (!Directory.Exists(webRoot))
        {
            return [];
        }

        List<string> violations = [];

        foreach (string file in EnumerateSourceFiles(webRoot)
                     .Where(file =>
                         string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetExtension(file), ".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(
                $"{Path.GetRelativePath(repositoryRoot, file)} is backend source inside "
                + "src/Vistara.Web; keep the Web tree frontend-only.");
        }

        foreach (string projectDirectory in Directory
                     .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
                     .Where(file => !IsIgnored(file))
                     .Select(file => Path.GetDirectoryName(file)!)
                     .Where(directory => !IsUnder(directory, webRoot))
                     .Distinct(StringComparer.Ordinal))
        {
            foreach (string file in EnumerateSourceFiles(projectDirectory)
                         .Where(file => FrontendSourceExtensions.Contains(Path.GetExtension(file))))
            {
                violations.Add(
                    $"{Path.GetRelativePath(repositoryRoot, file)} is frontend source inside "
                    + $"backend project {Path.GetFileName(projectDirectory)}; move it under "
                    + "src/Vistara.Web.");
            }
        }

        foreach (string projectFile in Directory
                     .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
                     .Where(file => !IsIgnored(file))
                     .Where(file => !IsUnder(file, webRoot)))
        {
            ValidateProjectItemsDoNotReachWeb(repositoryRoot, webRoot, projectFile, violations);
        }

        return violations
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateProjectItemsDoNotReachWeb(
        string repositoryRoot,
        string webRoot,
        string projectFile,
        List<string> violations)
    {
        XDocument document = XDocument.Load(projectFile);
        string projectDirectory = Path.GetDirectoryName(projectFile)!;
        string[] sourceItemNames =
        [
            "AdditionalFiles",
            "Compile",
            "Content",
            "EmbeddedResource",
            "None",
        ];

        foreach (XElement item in document
                     .Descendants()
                     .Where(element => sourceItemNames.Contains(
                         element.Name.LocalName,
                         StringComparer.Ordinal)))
        {
            string? include = (string?)item.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)
                || include.Contains("$(", StringComparison.Ordinal))
            {
                continue;
            }

            string stablePrefix = include.Split('*', '?')[0];
            string resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, stablePrefix));
            if (IsUnder(resolvedPath, webRoot))
            {
                violations.Add(
                    $"{Path.GetRelativePath(repositoryRoot, projectFile)} includes Web source "
                    + $"through '{include}'; backend projects must not compile or embed frontend source.");
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        return Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !IsIgnored(file));
    }

    private static bool IsIgnored(string path)
    {
        return path
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "dist", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, string directory)
    {
        string relativePath = Path.GetRelativePath(directory, path);
        return relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }
}
