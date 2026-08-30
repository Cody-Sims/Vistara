namespace Vistara.PerformanceTests;

internal enum HarnessMode
{
    Smoke,
    Benchmark,
    Evaluate,
}

internal sealed record HarnessOptions(
    HarnessMode Mode,
    int Samples,
    string OutputPath,
    string? MeasurementsPath,
    string? FrontendObservationsPath,
    bool RequireReference)
{
    internal static HarnessOptions Parse(string[] args, ProjectPaths paths)
    {
        HarnessMode mode = HarnessMode.Smoke;
        int samples = 5;
        string output = Path.Combine(paths.ArtifactsDirectory, "performance-report.json");
        string? measurements = null;
        string? frontend = null;
        bool requireReference = false;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            switch (argument)
            {
                case "--mode":
                    mode = ReadValue(args, ref index, argument) switch
                    {
                        "smoke" => HarnessMode.Smoke,
                        "benchmark" => HarnessMode.Benchmark,
                        "evaluate" => HarnessMode.Evaluate,
                        _ => throw new ArgumentException("Mode must be smoke, benchmark, or evaluate."),
                    };
                    break;
                case "--samples":
                    if (!int.TryParse(
                            ReadValue(args, ref index, argument),
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out samples) ||
                        samples is < 3 or > 30)
                    {
                        throw new ArgumentException("Samples must be between 3 and 30.");
                    }

                    break;
                case "--output":
                    output = paths.ResolveOwnedPath(ReadValue(args, ref index, argument));
                    break;
                case "--measurements":
                    measurements = Path.GetFullPath(ReadValue(args, ref index, argument));
                    break;
                case "--frontend-observations":
                    frontend = Path.GetFullPath(ReadValue(args, ref index, argument));
                    break;
                case "--require-reference":
                    requireReference = true;
                    break;
                case "--help":
                case "-h":
                    throw new HarnessHelpException();
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        return new HarnessOptions(
            mode,
            samples,
            Path.GetFullPath(output),
            measurements,
            frontend,
            requireReference);
    }

    private static string ReadValue(string[] args, ref int index, string argument)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{argument} requires a value.");
        }

        return args[index];
    }
}

internal sealed class HarnessHelpException : Exception;

internal sealed class ProjectPaths
{
    private ProjectPaths(string repositoryRoot)
    {
        RepositoryRoot = repositoryRoot;
        ProjectDirectory = Path.Combine(
            repositoryRoot,
            "tests",
            "Vistara.PerformanceTests");
        ArtifactsDirectory = Path.Combine(ProjectDirectory, "artifacts");
    }

    internal string RepositoryRoot { get; }

    internal string ProjectDirectory { get; }

    internal string ArtifactsDirectory { get; }

    internal string ResolveOwnedPath(string path)
    {
        string resolved = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(RepositoryRoot, path));
        string ownedRoot = ProjectDirectory + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(ownedRoot, StringComparison.Ordinal) &&
            !string.Equals(resolved, ProjectDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Output paths must remain beneath tests/Vistara.PerformanceTests.");
        }

        return resolved;
    }

    internal static ProjectPaths Discover()
    {
        string? configured = Environment.GetEnvironmentVariable("VISTARA_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) &&
            File.Exists(Path.Combine(configured, "Vistara.slnx")))
        {
            return new ProjectPaths(Path.GetFullPath(configured));
        }

        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
                {
                    return new ProjectPaths(directory.FullName);
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Unable to locate the Vistara repository root.");
    }
}
