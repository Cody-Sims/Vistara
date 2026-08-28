using Xunit;

namespace Vistara.ArchitectureTests;

public sealed class ProjectGraphPolicyTests
{
    [Fact]
    public void Forbidden_project_reference_has_actionable_diagnostic()
    {
        ProjectNode domain = ProjectNode.Create(
            "Vistara.Domain",
            projectReferences: ["Vistara.Persistence"]);
        ProjectNode persistence = ProjectNode.Create("Vistara.Persistence");

        IReadOnlyList<string> violations = ProjectGraphPolicy.Validate(
            [domain, persistence]);

        string violation = Assert.Single(violations);
        Assert.Contains("Vistara.Domain", violation, StringComparison.Ordinal);
        Assert.Contains("Vistara.Persistence", violation, StringComparison.Ordinal);
        Assert.Contains("allowed: no Vistara projects", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Dependency_cycle_reports_the_complete_cycle()
    {
        ProjectNode application = ProjectNode.Create(
            "Vistara.Application",
            projectReferences: ["Vistara.Domain"]);
        ProjectNode domain = ProjectNode.Create(
            "Vistara.Domain",
            projectReferences: ["Vistara.Persistence"]);
        ProjectNode persistence = ProjectNode.Create(
            "Vistara.Persistence",
            projectReferences: ["Vistara.Application"]);

        IReadOnlyList<string> violations = ProjectGraphPolicy.Validate(
            [application, domain, persistence]);

        Assert.Contains(
            violations,
            violation => violation.Contains(
                "Vistara.Application -> Vistara.Domain -> Vistara.Persistence -> Vistara.Application",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Core_layers_reject_framework_and_infrastructure_dependencies()
    {
        ProjectNode domain = ProjectNode.Create(
            "Vistara.Domain",
            packageReferences: ["Microsoft.EntityFrameworkCore"]);
        ProjectNode application = ProjectNode.Create(
            "Vistara.Application",
            packageReferences: ["Azure.Storage.Blobs"]);
        ProjectNode contracts = ProjectNode.Create(
            "Vistara.Contracts",
            frameworkReferences: ["Microsoft.AspNetCore.App"]);

        IReadOnlyList<string> violations = ProjectGraphPolicy.Validate(
            [domain, application, contracts]);

        Assert.Contains(
            violations,
            violation => violation.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Contains("Azure.Storage.Blobs", StringComparison.Ordinal));
        Assert.Contains(
            violations,
            violation => violation.Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_project_reference_to_test_project_is_rejected()
    {
        ProjectNode domain = ProjectNode.Create(
            "Vistara.Domain",
            projectReferences: ["Vistara.UnitTests"]);
        ProjectNode tests = ProjectNode.Create(
            "Vistara.UnitTests",
            isProduction: false,
            isTestProject: true);

        IReadOnlyList<string> violations = ProjectGraphPolicy.Validate([domain, tests]);

        Assert.Contains(
            violations,
            violation => violation.Contains(
                "production projects must never depend on tests",
                StringComparison.Ordinal));
    }
}
