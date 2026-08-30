using System.Text.Json;

namespace Vistara.PerformanceTests;

internal static class ImportedMeasurements
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    internal static async Task<(
        IReadOnlyDictionary<string, Measurement> Measurements,
        IReadOnlyList<PrerequisiteResult> Prerequisites)> ReadAsync(
        string? measurementsPath,
        string? frontendObservationsPath,
        CancellationToken cancellationToken)
    {
        var measurements = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        var prerequisites = new List<PrerequisiteResult>();
        await MergeAsync(measurementsPath, measurements, prerequisites, cancellationToken);
        await MergeAsync(
            frontendObservationsPath,
            measurements,
            prerequisites,
            cancellationToken);

        if (measurementsPath is null)
        {
            prerequisites.Add(new PrerequisiteResult(
                "reference measurements",
                BudgetStatus.Unavailable,
                "--measurements was not supplied."));
        }

        if (frontendObservationsPath is null)
        {
            prerequisites.Add(new PrerequisiteResult(
                "frontend observations",
                BudgetStatus.Unavailable,
                "--frontend-observations was not supplied."));
        }

        return (measurements, prerequisites);
    }

    private static async Task MergeAsync(
        string? path,
        Dictionary<string, Measurement> measurements,
        List<PrerequisiteResult> prerequisites,
        CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return;
        }

        if (!File.Exists(path))
        {
            prerequisites.Add(new PrerequisiteResult(
                Path.GetFileName(path),
                BudgetStatus.Unavailable,
                $"Measurement file does not exist: {path}"));
            return;
        }

        await using FileStream stream = File.OpenRead(path);
        ImportedDocument? document = await JsonSerializer.DeserializeAsync<ImportedDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (document?.Measurements is null)
        {
            throw new InvalidDataException(
                $"Measurement file does not contain a measurements object: {path}");
        }

        foreach ((string id, ImportedMeasurement imported) in document.Measurements)
        {
            measurements[id] = imported.Value is double value
                ? Measurement.Available(value)
                : !string.IsNullOrWhiteSpace(imported.UnavailableReason)
                    ? Measurement.Unavailable(imported.UnavailableReason)
                    : Measurement.Skipped(
                        imported.SkippedReason ?? "The imported run skipped this measurement.");
        }

        if (document.Prerequisites is not null)
        {
            foreach (ImportedPrerequisite prerequisite in document.Prerequisites)
            {
                prerequisites.Add(new PrerequisiteResult(
                    prerequisite.Name,
                    ParseStatus(prerequisite.Status),
                    prerequisite.Detail));
            }
        }
    }

    private static BudgetStatus ParseStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "passed" => BudgetStatus.Passed,
            "failed" => BudgetStatus.Failed,
            "skipped" => BudgetStatus.Skipped,
            "unavailable" => BudgetStatus.Unavailable,
            _ => throw new InvalidDataException($"Unknown prerequisite status: {status}"),
        };

    private sealed record ImportedDocument(
        IReadOnlyDictionary<string, ImportedMeasurement>? Measurements,
        IReadOnlyList<ImportedPrerequisite>? Prerequisites);

    private sealed record ImportedMeasurement(
        double? Value,
        string? UnavailableReason,
        string? SkippedReason);

    private sealed record ImportedPrerequisite(
        string Name,
        string Status,
        string Detail);
}
