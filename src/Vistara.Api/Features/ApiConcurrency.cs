using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Vistara.Api.Features;

public enum IfMatchKind
{
    /// <summary>No <c>If-Match</c> header was sent.</summary>
    Missing,

    /// <summary>The header was present but is not a Vistara entity tag.</summary>
    Malformed,

    /// <summary>The caller sent <c>*</c> and accepts the current version.</summary>
    Wildcard,

    /// <summary>The caller sent a concrete <c>"v{version}"</c> tag.</summary>
    Version,
}

public readonly record struct IfMatchCondition(IfMatchKind Kind, long Version)
{
    public bool Matches(long current) =>
        Kind == IfMatchKind.Wildcard ||
        (Kind == IfMatchKind.Version && Version == current);
}

/// <summary>
/// Shared entity-tag handling. Every mutable aggregate publishes
/// <c>ETag: "v{version}"</c> and every single-resource mutation requires a
/// matching <c>If-Match</c>.
/// </summary>
public static class ApiConcurrency
{
    public static string ToETag(long version) => $"\"v{version}\"";

    public static IfMatchCondition ReadIfMatch(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var values) ||
            values.Count == 0)
        {
            return new IfMatchCondition(IfMatchKind.Missing, 0);
        }

        string[] candidates = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(','))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();
        if (candidates.Length != 1)
        {
            return new IfMatchCondition(
                candidates.Length == 0 ? IfMatchKind.Missing : IfMatchKind.Malformed,
                0);
        }

        string candidate = candidates[0];
        if (candidate == "*")
        {
            return new IfMatchCondition(IfMatchKind.Wildcard, 0);
        }

        if (candidate.Length > 3 &&
            candidate[0] == '"' &&
            candidate[^1] == '"' &&
            candidate[1] == 'v' &&
            long.TryParse(
                candidate[2..^1],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long version))
        {
            return new IfMatchCondition(IfMatchKind.Version, version);
        }

        return new IfMatchCondition(IfMatchKind.Malformed, 0);
    }

    /// <summary>
    /// Writes the standard problem for an absent or malformed precondition and
    /// reports whether the caller may proceed.
    /// </summary>
    public static async Task<bool> RequirePreconditionAsync(
        HttpContext context,
        IfMatchCondition condition,
        string codePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(codePrefix);
        switch (condition.Kind)
        {
            case IfMatchKind.Missing:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status428PreconditionRequired,
                    $"{codePrefix}.if_match_required",
                    "This mutation requires an If-Match entity tag.",
                    cancellationToken);
                return false;
            case IfMatchKind.Malformed:
                await ApiProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    $"{codePrefix}.if_match_malformed",
                    "The If-Match header must be a single \"v{version}\" entity tag.",
                    cancellationToken);
                return false;
            default:
                return true;
        }
    }

    public static Task WriteStaleAsync(
        HttpContext context,
        string codePrefix,
        CancellationToken cancellationToken) =>
        ApiProblemWriter.WriteAsync(
            context,
            StatusCodes.Status412PreconditionFailed,
            $"{codePrefix}.stale_version",
            "The resource changed since it was read; reload and reapply the edit.",
            cancellationToken);
}
