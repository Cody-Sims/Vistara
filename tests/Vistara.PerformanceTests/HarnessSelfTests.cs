namespace Vistara.PerformanceTests;

internal static class HarnessSelfTests
{
    internal static void Run()
    {
        PercentileUsesNearestRank();
        RequiredMeasurementOverBudgetFails();
        MissingPrerequisiteIsUnavailable();
        BenchmarkTypesAreRunnable();
    }

    private static void PercentileUsesNearestRank()
    {
        double percentile = Statistics.Percentile([1, 2, 3, 4, 100], 95);
        Require(percentile == 100, "p95 must use a deterministic nearest-rank value.");
    }

    private static void RequiredMeasurementOverBudgetFails()
    {
        BudgetResult result = BudgetEvaluator.Evaluate(
            new BudgetDefinition("cold-transform-p95-ms", 750, "ms", true),
            Measurement.Available(751));
        Require(result.Status == BudgetStatus.Failed, "Over-budget results must fail.");
    }

    private static void MissingPrerequisiteIsUnavailable()
    {
        BudgetResult result = BudgetEvaluator.Evaluate(
            new BudgetDefinition("warm-derivative-ttfb-p95-ms", 100, "ms", true),
            Measurement.Unavailable("VISTARA_DERIVATIVE_URL is not configured."));
        Require(
            result.Status == BudgetStatus.Unavailable,
            "Missing prerequisites must not be reported as passing.");
    }

    private static void BenchmarkTypesAreRunnable()
    {
        Require(
            !typeof(ManagementReadBenchmarks).IsSealed &&
            !typeof(ColdTransformBenchmarks).IsSealed,
            "BenchmarkDotNet benchmark declaring types must not be sealed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
