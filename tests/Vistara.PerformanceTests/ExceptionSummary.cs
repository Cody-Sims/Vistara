namespace Vistara.PerformanceTests;

internal static class ExceptionSummary
{
    internal static string Create(string prefix, Exception exception)
    {
        string message = exception.GetBaseException().Message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? exception.GetType().Name;
        const int maximumLength = 300;
        if (message.Length > maximumLength)
        {
            message = string.Concat(message.AsSpan(0, maximumLength), "…");
        }

        return $"{prefix}: {message}";
    }
}
