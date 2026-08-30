using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vistara.PerformanceTests;

internal static class PerformanceHarness
{
    internal static async Task<int> RunAsync(string[] args)
    {
        ProjectPaths paths;
        HarnessOptions options;
        try
        {
            paths = ProjectPaths.Discover();
            options = HarnessOptions.Parse(args, paths);
        }
        catch (HarnessHelpException)
        {
            PrintHelp();
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            PrintHelp();
            return 2;
        }

        HarnessSelfTests.Run();
        Directory.CreateDirectory(paths.ArtifactsDirectory);
        Environment.SetEnvironmentVariable("VISTARA_REPOSITORY_ROOT", paths.RepositoryRoot);

        IReadOnlyDictionary<string, Measurement> measurements;
        IReadOnlyList<PrerequisiteResult> prerequisites;
        switch (options.Mode)
        {
            case HarnessMode.Smoke:
                (measurements, prerequisites) =
                    await SmokeScenarios.RunAsync(paths, options, CancellationToken.None);
                break;
            case HarnessMode.Benchmark:
                (measurements, prerequisites) =
                    await BenchmarkScenarios.RunAsync(paths, CancellationToken.None);
                break;
            case HarnessMode.Evaluate:
                (measurements, prerequisites) =
                    await ImportedMeasurements.ReadAsync(
                        options.MeasurementsPath,
                        options.FrontendObservationsPath,
                        CancellationToken.None);
                break;
            default:
                throw new InvalidOperationException("The performance mode is unsupported.");
        }

        BudgetDefinition[] budgets = BudgetCatalog.All;
        BudgetResult[] results = budgets
            .Select(budget => BudgetEvaluator.Evaluate(
                budget,
                measurements.TryGetValue(budget.Id, out Measurement? measurement)
                    ? measurement
                    : Measurement.Skipped($"No {options.Mode.ToString().ToLowerInvariant()} measurement was supplied.")))
            .ToArray();
        using Process process = Process.GetCurrentProcess();
        var report = new PerformanceReport(
            1,
            options.Mode.ToString().ToLowerInvariant(),
            DateTimeOffset.UtcNow,
            results,
            prerequisites,
            new PerformanceEnvironment(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                process.WorkingSet64));
        await ReportWriter.WriteAsync(options.OutputPath, report, CancellationToken.None);
        PrintSummary(options.OutputPath, results, prerequisites);

        bool failed = results.Any(result =>
            result.Required && result.Status == BudgetStatus.Failed);
        bool requiredUnavailable = options.RequireReference &&
            results.Any(result =>
                result.Required &&
                result.Status is BudgetStatus.Unavailable or BudgetStatus.Skipped);
        bool prerequisiteFailed = prerequisites.Any(result =>
            result.Status == BudgetStatus.Failed);
        return failed || requiredUnavailable || prerequisiteFailed ? 1 : 0;
    }

    private static void PrintSummary(
        string outputPath,
        IReadOnlyList<BudgetResult> budgets,
        IReadOnlyList<PrerequisiteResult> prerequisites)
    {
        foreach (BudgetResult budget in budgets)
        {
            string observed = budget.Value?.ToString(
                "0.###",
                System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine(
                $"{budget.Status,-11} {budget.Id}: {observed}/{budget.Maximum:0.###} {budget.Unit}" +
                (budget.Detail is null ? string.Empty : $" ({budget.Detail})"));
        }

        foreach (PrerequisiteResult prerequisite in prerequisites)
        {
            Console.WriteLine(
                $"{prerequisite.Status,-11} prerequisite {prerequisite.Name}: {prerequisite.Detail}");
        }

        Console.WriteLine($"Report: {outputPath}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Vistara performance budget harness
              --mode smoke|benchmark|evaluate   Default: smoke
              --samples 3..30                  Smoke sample count; default: 5
              --output <owned-path>             JSON report path
              --measurements <json>             Imported k6/reference measurements
              --frontend-observations <json>    Imported browser observations
              --require-reference               Fail on skipped/unavailable required budgets
            """);
    }
}
