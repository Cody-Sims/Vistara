using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.PerformanceTests;

internal enum BudgetStatus
{
    Passed,
    Failed,
    Skipped,
    Unavailable,
}

internal sealed record BudgetDefinition(
    string Id,
    double Maximum,
    string Unit,
    bool Required);

internal sealed record Measurement(
    double? Value,
    string? UnavailableReason,
    string? SkippedReason)
{
    internal static Measurement Available(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Performance measurements must be finite and non-negative.");
        }

        return new Measurement(value, null, null);
    }

    internal static Measurement Unavailable(string reason) => new(null, reason, null);

    internal static Measurement Skipped(string reason) => new(null, null, reason);
}

internal sealed record BudgetResult(
    string Id,
    double Maximum,
    string Unit,
    bool Required,
    double? Value,
    BudgetStatus Status,
    string? Detail);

internal static class BudgetEvaluator
{
    internal static BudgetResult Evaluate(
        BudgetDefinition budget,
        Measurement measurement)
    {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(measurement);

        if (measurement.Value is double value)
        {
            return new BudgetResult(
                budget.Id,
                budget.Maximum,
                budget.Unit,
                budget.Required,
                value,
                value <= budget.Maximum ? BudgetStatus.Passed : BudgetStatus.Failed,
                null);
        }

        if (!string.IsNullOrWhiteSpace(measurement.UnavailableReason))
        {
            return new BudgetResult(
                budget.Id,
                budget.Maximum,
                budget.Unit,
                budget.Required,
                null,
                BudgetStatus.Unavailable,
                measurement.UnavailableReason);
        }

        return new BudgetResult(
            budget.Id,
            budget.Maximum,
            budget.Unit,
            budget.Required,
            null,
            BudgetStatus.Skipped,
            measurement.SkippedReason ?? "The measurement was not requested.");
    }
}

internal static class Statistics
{
    internal static double Percentile(IEnumerable<double> samples, double percentile)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (percentile is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        double[] ordered = samples.Order().ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one sample is required.", nameof(samples));
        }

        int rank = (int)Math.Ceiling(percentile / 100 * ordered.Length);
        return ordered[Math.Max(rank - 1, 0)];
    }
}

internal sealed record PrerequisiteResult(
    string Name,
    BudgetStatus Status,
    string Detail);

internal static class ExitCodeEvaluator
{
    internal static bool ShouldFail(
        IReadOnlyList<BudgetResult> budgets,
        IReadOnlyList<PrerequisiteResult> prerequisites,
        bool requireReference) =>
        budgets.Any(result =>
            result.Required && result.Status == BudgetStatus.Failed) ||
        prerequisites.Any(result => result.Status == BudgetStatus.Failed) ||
        requireReference &&
        (
            budgets.Any(result =>
                result.Required &&
                result.Status is BudgetStatus.Unavailable or BudgetStatus.Skipped) ||
            prerequisites.Any(result =>
                result.Status is BudgetStatus.Unavailable or BudgetStatus.Skipped)
        );
}

internal sealed record PerformanceEnvironment(
    string Framework,
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    long WorkingSetBytes);

internal sealed record PerformanceReport(
    int SchemaVersion,
    string Mode,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<BudgetResult> Budgets,
    IReadOnlyList<PrerequisiteResult> Prerequisites,
    PerformanceEnvironment Environment);

internal static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static async Task WriteAsync(
        string path,
        PerformanceReport report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            JsonOptions,
            cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
    }
}
