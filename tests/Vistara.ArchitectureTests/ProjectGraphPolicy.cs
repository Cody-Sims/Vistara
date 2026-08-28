namespace Vistara.ArchitectureTests;

internal static class ProjectGraphPolicy
{
    private static readonly Dictionary<string, HashSet<string>> ExactAllowedReferences =
        new(StringComparer.Ordinal)
        {
            ["Vistara.Domain"] = Allowed(),
            ["Vistara.Application"] = Allowed("Vistara.Domain"),
            ["Vistara.Persistence"] = Allowed("Vistara.Application", "Vistara.Domain"),
            ["Vistara.Imaging.NetVips"] = Allowed("Vistara.Application"),
            ["Vistara.Auth"] = Allowed("Vistara.Application", "Vistara.Persistence"),
            ["Vistara.Observability"] = Allowed(),
            ["Vistara.Migrations.Sqlite"] = Allowed("Vistara.Persistence"),
            ["Vistara.Migrations.Postgres"] = Allowed("Vistara.Persistence"),
            ["Vistara.Api"] = Allowed(
                "Vistara.Contracts",
                "Vistara.Application",
                "Vistara.Persistence",
                "Vistara.Storage.Local",
                "Vistara.Storage.Azure",
                "Vistara.Storage.S3",
                "Vistara.Imaging.NetVips",
                "Vistara.Auth",
                "Vistara.Observability"),
            ["Vistara.Worker"] = Allowed(
                "Vistara.Application",
                "Vistara.Persistence",
                "Vistara.Storage.Local",
                "Vistara.Storage.Azure",
                "Vistara.Storage.S3",
                "Vistara.Imaging.NetVips",
                "Vistara.Auth",
                "Vistara.Observability"),
        };

    private static readonly string[] InfrastructurePackagePrefixes =
    [
        "Amazon.",
        "AWSSDK.",
        "Azure.",
        "Dapper",
        "Microsoft.AspNetCore",
        "Microsoft.Data.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.",
        "Microsoft.Identity.",
        "MongoDB.",
        "NetVips",
        "Minio",
        "Npgsql",
        "OpenTelemetry",
        "Oracle.",
        "Serilog",
        "SixLabors.ImageSharp",
        "StackExchange.Redis",
        "System.IdentityModel.",
    ];

    private static readonly HashSet<string> InfrastructureProjects = Allowed(
        "Vistara.Auth",
        "Vistara.Imaging.NetVips",
        "Vistara.Migrations.Postgres",
        "Vistara.Migrations.Sqlite",
        "Vistara.Observability",
        "Vistara.Persistence",
        "Vistara.Storage.Azure",
        "Vistara.Storage.Local",
        "Vistara.Storage.S3");

    internal static IReadOnlyList<string> Validate(IReadOnlyCollection<ProjectNode> projects)
    {
        List<string> violations = [];
        Dictionary<string, ProjectNode> projectsByName = projects
            .ToDictionary(project => project.Name, StringComparer.Ordinal);

        ValidateKnownProjectReferences(projects, projectsByName, violations);
        ValidateProductionDoesNotReferenceTests(projects, projectsByName, violations);
        ValidateApprovedDirections(projects, violations);
        ValidateFrameworkAndPackageBoundaries(projects, violations);
        ValidateCompositionRoots(projects, violations);
        ValidateCycles(projects, projectsByName, violations);

        return violations
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateKnownProjectReferences(
        IEnumerable<ProjectNode> projects,
        Dictionary<string, ProjectNode> projectsByName,
        List<string> violations)
    {
        foreach (ProjectNode project in projects)
        {
            foreach (string reference in project.ProjectReferences)
            {
                if (reference.StartsWith("Vistara.", StringComparison.Ordinal)
                    && !projectsByName.ContainsKey(reference))
                {
                    violations.Add(
                        $"{project.ProjectFile}: {project.Name} references '{reference}', "
                        + "but that project was not found in the repository graph.");
                }
            }
        }
    }

    private static void ValidateProductionDoesNotReferenceTests(
        IEnumerable<ProjectNode> projects,
        Dictionary<string, ProjectNode> projectsByName,
        List<string> violations)
    {
        foreach (ProjectNode project in projects.Where(project => project.IsProduction))
        {
            foreach (string reference in project.ProjectReferences)
            {
                if (projectsByName.TryGetValue(reference, out ProjectNode? target)
                    && target.IsTestProject)
                {
                    violations.Add(
                        $"{project.ProjectFile}: production project {project.Name} references "
                        + $"test project {reference}; production projects must never depend on tests.");
                }
            }
        }
    }

    private static void ValidateApprovedDirections(
        IEnumerable<ProjectNode> projects,
        List<string> violations)
    {
        foreach (ProjectNode project in projects.Where(project => project.IsProduction))
        {
            if (project.Name == "Vistara.Contracts")
            {
                ValidateContracts(project, violations);
                continue;
            }

            HashSet<string>? allowed = GetAllowedReferences(project.Name);
            if (allowed is null)
            {
                violations.Add(
                    $"{project.ProjectFile}: production project {project.Name} is not represented "
                    + "in the approved architecture graph.");
                continue;
            }

            foreach (string reference in project.ProjectReferences
                         .Where(IsVistaraProject)
                         .Where(reference => !allowed.Contains(reference)))
            {
                string expected = allowed.Count == 0
                    ? "no Vistara projects"
                    : string.Join(", ", allowed.Order(StringComparer.Ordinal));
                violations.Add(
                    $"{project.ProjectFile}: {project.Name} references forbidden project "
                    + $"{reference}; allowed: {expected}.");
            }
        }
    }

    private static void ValidateContracts(
        ProjectNode contracts,
        List<string> violations)
    {
        foreach (string reference in contracts.ProjectReferences
                     .Where(InfrastructureProjects.Contains))
        {
            violations.Add(
                $"{contracts.ProjectFile}: Vistara.Contracts references infrastructure project "
                + $"{reference}; Contracts may depend only on non-infrastructure abstractions.");
        }
    }

    private static void ValidateFrameworkAndPackageBoundaries(
        IEnumerable<ProjectNode> projects,
        List<string> violations)
    {
        foreach (ProjectNode project in projects.Where(project => project.IsProduction))
        {
            if (project.Name is "Vistara.Domain" or "Vistara.Application" or "Vistara.Contracts")
            {
                foreach (string package in project.PackageReferences.Where(IsInfrastructurePackage))
                {
                    violations.Add(
                        $"{project.ProjectFile}: {project.Name} references infrastructure/framework "
                        + $"package {package}; keep this layer framework- and infrastructure-free.");
                }

                foreach (string framework in project.FrameworkReferences)
                {
                    violations.Add(
                        $"{project.ProjectFile}: {project.Name} references framework {framework}; "
                        + "keep this layer framework- and infrastructure-free.");
                }

                if (project.Sdk.Contains(".Web", StringComparison.Ordinal)
                    || project.Sdk.Contains(".Worker", StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{project.ProjectFile}: {project.Name} uses composition-root SDK "
                        + $"{project.Sdk}; use Microsoft.NET.Sdk for this layer.");
                }
            }

            if (project.Name == "Vistara.Observability")
            {
                foreach (string package in project.PackageReferences.Where(package =>
                             !package.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                             && !package.StartsWith("OpenTelemetry", StringComparison.OrdinalIgnoreCase)
                             && !package.StartsWith("System.", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(
                        $"{project.ProjectFile}: Vistara.Observability references {package}; "
                        + "only framework and OpenTelemetry packages are approved.");
                }
            }
        }
    }

    private static void ValidateCompositionRoots(
        IEnumerable<ProjectNode> projects,
        List<string> violations)
    {
        ProjectNode[] productionProjects = projects
            .Where(project => project.IsProduction)
            .ToArray();

        ValidateCompositionRootSdk(
            productionProjects,
            "Vistara.Api",
            ".Web",
            violations);
        ValidateCompositionRootSdk(
            productionProjects,
            "Vistara.Worker",
            ".Worker",
            violations);

        foreach (ProjectNode project in productionProjects.Where(project =>
                     project.Name is not "Vistara.Api" and not "Vistara.Worker"))
        {
            foreach (string root in project.ProjectReferences.Where(reference =>
                         reference is "Vistara.Api" or "Vistara.Worker"))
            {
                violations.Add(
                    $"{project.ProjectFile}: {project.Name} references composition root {root}; "
                    + "API and Worker must remain terminal executable projects.");
            }
        }
    }

    private static void ValidateCompositionRootSdk(
        IEnumerable<ProjectNode> projects,
        string projectName,
        string requiredSdkFragment,
        List<string> violations)
    {
        ProjectNode? project = projects.SingleOrDefault(project => project.Name == projectName);
        if (project is not null
            && !project.Sdk.Contains(requiredSdkFragment, StringComparison.Ordinal))
        {
            violations.Add(
                $"{project.ProjectFile}: {projectName} must use an executable composition-root SDK "
                + $"containing '{requiredSdkFragment}', but uses '{project.Sdk}'.");
        }
    }

    private static void ValidateCycles(
        IEnumerable<ProjectNode> projects,
        Dictionary<string, ProjectNode> projectsByName,
        List<string> violations)
    {
        HashSet<string> visited = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        List<string> path = [];
        HashSet<string> reportedCycles = new(StringComparer.Ordinal);

        foreach (ProjectNode project in projects.OrderBy(project => project.Name, StringComparer.Ordinal))
        {
            Visit(project.Name);
        }

        void Visit(string projectName)
        {
            if (visited.Contains(projectName)
                || !projectsByName.TryGetValue(projectName, out ProjectNode? project))
            {
                return;
            }

            if (!visiting.Add(projectName))
            {
                int cycleStart = path.IndexOf(projectName);
                string cycle = string.Join(" -> ", path.Skip(cycleStart).Append(projectName));
                if (reportedCycles.Add(cycle))
                {
                    violations.Add($"Project reference cycle detected: {cycle}.");
                }

                return;
            }

            path.Add(projectName);
            foreach (string reference in project.ProjectReferences
                         .Where(projectsByName.ContainsKey)
                         .Order(StringComparer.Ordinal))
            {
                Visit(reference);
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(projectName);
            visited.Add(projectName);
        }
    }

    private static HashSet<string>? GetAllowedReferences(string projectName)
    {
        if (projectName.StartsWith("Vistara.Storage.", StringComparison.Ordinal))
        {
            return Allowed("Vistara.Application");
        }

        return ExactAllowedReferences.GetValueOrDefault(projectName);
    }

    private static bool IsInfrastructurePackage(string package)
    {
        return InfrastructurePackagePrefixes.Any(prefix =>
            package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVistaraProject(string projectName)
    {
        return projectName.StartsWith("Vistara.", StringComparison.Ordinal);
    }

    private static HashSet<string> Allowed(params string[] projects)
    {
        return projects.ToHashSet(StringComparer.Ordinal);
    }
}
