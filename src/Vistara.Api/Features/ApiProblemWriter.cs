using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Vistara.Contracts.Errors;
using Vistara.Domain.Common;

namespace Vistara.Api.Features;

/// <summary>
/// Writes RFC 9457 <c>application/problem+json</c> responses using the stable
/// Vistara problem type and error-code conventions shared by every feature slice.
/// </summary>
public static class ApiProblemWriter
{
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int StatusFor(ErrorCategory category) =>
        category switch
        {
            ErrorCategory.Validation => StatusCodes.Status422UnprocessableEntity,
            ErrorCategory.NotFound => StatusCodes.Status404NotFound,
            ErrorCategory.Conflict => StatusCodes.Status409Conflict,
            ErrorCategory.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorCategory.Forbidden => StatusCodes.Status403Forbidden,
            ErrorCategory.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

    public static Task WriteResultErrorAsync(
        HttpContext context,
        ResultError error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(error);
        return WriteAsync(
            context,
            StatusFor(error.Category),
            error.Code,
            error.Message,
            cancellationToken);
    }

    public static async Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var problem = new ApiProblemDetails(
            $"https://vistara.dev/problems/{code.Replace('.', '-')}",
            title,
            status,
            new ErrorCode(code.Replace('.', '_')),
            traceId: context.TraceIdentifier,
            errors: errors);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            ProblemJsonOptions,
            cancellationToken);
    }
}
