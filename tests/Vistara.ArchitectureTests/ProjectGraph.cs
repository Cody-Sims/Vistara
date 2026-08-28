using System.Xml.Linq;

namespace Vistara.ArchitectureTests;

internal sealed record ProjectNode(
    string Name,
    string ProjectFile,
    string Sdk,
    bool IsProduction,
    bool IsTestProject,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> FrameworkReferences)
{
    internal static ProjectNode Create(
        string name,
        IReadOnlyList<string>? projectReferences = null,
        IReadOnlyList<string>? packageReferences = null,
        IReadOnlyList<string>? frameworkReferences = null,
        string sdk = "Microsoft.NET.Sdk",
        bool isProduction = true,
        bool isTestProject = false)
    {
        return new ProjectNode(
            name,
            $"{name}.csproj",
            sdk,
            isProduction,
            isTestProject,
            projectReferences ?? [],
            packageReferences ?? [],
            frameworkReferences ?? []);
    }
}

internal static class ProjectGraphLoader
{
    internal static IReadOnlyList<ProjectNode> Load(string repositoryRoot)
    {
        string sourceRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "src"));
        string testsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, "tests"));

        return Directory
            .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .Select(path => LoadProject(path, sourceRoot, testsRoot))
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProjectNode LoadProject(
        string projectFile,
        string sourceRoot,
        string testsRoot)
    {
        string fullProjectFile = Path.GetFullPath(projectFile);
        XDocument document = XDocument.Load(fullProjectFile);
        XElement root = document.Root
            ?? throw new InvalidOperationException($"Project '{projectFile}' has no root element.");
        string projectDirectory = Path.GetDirectoryName(fullProjectFile)
            ?? throw new InvalidOperationException($"Project '{projectFile}' has no directory.");

        string[] projectReferences = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(
                Path.GetFullPath(Path.Combine(projectDirectory, include!))))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] packageReferences = GetIncludes(document, "PackageReference");
        string[] frameworkReferences = GetIncludes(document, "FrameworkReference");
        bool isTestProject =
            IsUnder(fullProjectFile, testsRoot)
            || document
                .Descendants()
                .Any(element =>
                    element.Name.LocalName == "IsTestProject"
                    && string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

        return new ProjectNode(
            Path.GetFileNameWithoutExtension(fullProjectFile),
            Path.GetRelativePath(FindRepositoryRoot(sourceRoot), fullProjectFile),
            (string?)root.Attribute("Sdk") ?? string.Empty,
            IsUnder(fullProjectFile, sourceRoot),
            isTestProject,
            projectReferences,
            packageReferences,
            frameworkReferences);
    }

    private static string[] GetIncludes(XDocument document, string itemName)
    {
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == itemName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsBuildOutput(string path)
    {
        return path
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "node_modules", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUnder(string path, string directory)
    {
        string relativePath = Path.GetRelativePath(directory, path);
        return relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private static string FindRepositoryRoot(string sourceRoot)
    {
        return Directory.GetParent(sourceRoot)?.FullName
            ?? throw new InvalidOperationException($"Cannot find repository root above '{sourceRoot}'.");
    }
}

internal static class RepositoryLayout
{
    internal static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx"))
                && Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the Vistara repository root above '{AppContext.BaseDirectory}'.");
    }
}
