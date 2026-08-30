using Vistara.PerformanceTests;

try
{
    return await PerformanceHarness.RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine(
        ExceptionSummary.Create("Performance harness failed", exception));
    return 2;
}
