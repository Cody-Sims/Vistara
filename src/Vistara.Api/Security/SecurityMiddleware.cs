using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Vistara.Api.Composition.Security;

namespace Vistara.Api.Security;

internal sealed class VistaraSecurityMiddleware(
    RequestDelegate next,
    IOptions<VistaraSecurityOptions> options,
    PartitionedRateLimiter<HttpContext> rateLimiter,
    IHostEnvironment environment,
    ILogger<VistaraSecurityMiddleware> logger)
{
    private const string ProblemContentType = "application/problem+json";
    private readonly VistaraSecurityOptions _options = options.Value;
    private readonly SecurityCorsPolicy _cors = new(options.Value.Cors);

    public async Task InvokeAsync(HttpContext context)
    {
        ApplySecurityHeaders(context, environment);
        try
        {
            if (!await _cors.ApplyAsync(context))
            {
                return;
            }

            SecurityLimitFailure? limitFailure =
                SecurityRequestLimits.Validate(context, _options.Limits);
            if (limitFailure is not null)
            {
                await SecurityProblemWriter.WriteAsync(
                    context,
                    limitFailure.Status,
                    limitFailure.Code,
                    limitFailure.Title);
                return;
            }

            if (SecurityRequestClassifier.IsRateLimited(context))
            {
                using RateLimitLease lease =
                    await rateLimiter.AcquireAsync(context, 1, context.RequestAborted);
                if (!lease.IsAcquired)
                {
                    if (lease.TryGetMetadata(
                            MetadataName.RetryAfter,
                            out TimeSpan retryAfter))
                    {
                        context.Response.Headers.RetryAfter = Math.Max(
                                1,
                                (int)Math.Ceiling(retryAfter.TotalSeconds))
                            .ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        context.Response.Headers.RetryAfter = Math.Max(
                                1,
                                (int)Math.Ceiling(
                                    _options.Limits.RateLimitWindow.TotalSeconds))
                            .ToString(CultureInfo.InvariantCulture);
                    }

                    await SecurityProblemWriter.WriteAsync(
                        context,
                        StatusCodes.Status429TooManyRequests,
                        "rate_limit.exceeded",
                        "Too many requests");
                    return;
                }
            }

            await next(context);
            await NormalizeMalformedResponseAsync(context);
        }
        catch (BadHttpRequestException error)
            when (!context.Response.HasStarted)
        {
            int status = error.StatusCode is
                StatusCodes.Status413PayloadTooLarge
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status400BadRequest;
            await SecurityProblemWriter.WriteAsync(
                context,
                status,
                status == StatusCodes.Status413PayloadTooLarge
                    ? "request.body_too_large"
                    : "request.malformed",
                status == StatusCodes.Status413PayloadTooLarge
                    ? "The request body is too large"
                    : "The request is malformed");
        }
        catch (JsonException) when (!context.Response.HasStarted)
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                "request.malformed",
                "The request is malformed");
        }
        finally
        {
            SecurityLog.RequestCompleted(
                logger,
                context.Request.Method,
                SecurityRequestClassifier.LogTarget(context),
                context.Response.StatusCode);
        }
    }

    private static void ApplySecurityHeaders(
        HttpContext context,
        IHostEnvironment environment)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] =
            "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        context.Response.Headers["Content-Security-Policy"] =
            environment.IsDevelopment()
                ? SecurityHeaderValues.DevelopmentContentSecurityPolicy
                : SecurityHeaderValues.ProductionContentSecurityPolicy;
        if (!environment.IsDevelopment())
        {
            context.Response.Headers["Strict-Transport-Security"] =
                "max-age=31536000; includeSubDomains";
        }
    }

    private static async Task NormalizeMalformedResponseAsync(
        HttpContext context)
    {
        if (context.Response.StatusCode != StatusCodes.Status400BadRequest ||
            context.Response.HasStarted ||
            string.Equals(
                context.Response.ContentType,
                ProblemContentType,
                StringComparison.OrdinalIgnoreCase) ||
            !ResponseBodyIsEmpty(context.Response))
        {
            return;
        }

        await SecurityProblemWriter.WriteAsync(
            context,
            StatusCodes.Status400BadRequest,
            "request.malformed",
            "The request is malformed");
    }

    private static bool ResponseBodyIsEmpty(HttpResponse response) =>
        response.Body.CanSeek
            ? response.Body.Length == 0
            : response.ContentLength is null or 0;
}

internal static class SecurityHeaderValues
{
    internal const string ProductionContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data: blob:; media-src 'self' blob:; " +
        "connect-src 'self'; script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
        "worker-src 'self' blob:; manifest-src 'self'";

    internal const string DevelopmentContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data: blob:; media-src 'self' blob:; " +
        "connect-src 'self' http: https: ws: wss:; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; font-src 'self' data:; " +
        "worker-src 'self' blob:; manifest-src 'self'";
}

internal sealed record SecurityLimitFailure(
    int Status,
    string Code,
    string Title);

internal static class SecurityRequestLimits
{
    internal static SecurityLimitFailure? Validate(
        HttpContext context,
        SecurityLimitOptions options)
    {
        int targetBytes = Encoding.UTF8.GetByteCount(
            context.Request.PathBase.Value ?? string.Empty) +
            Encoding.UTF8.GetByteCount(context.Request.Path.Value ?? string.Empty) +
            Encoding.UTF8.GetByteCount(
                context.Request.QueryString.Value ?? string.Empty);
        if (targetBytes > options.MaxRequestTargetBytes)
        {
            return new(
                StatusCodes.Status414UriTooLong,
                "request.target_too_long",
                "The request target is too long");
        }

        if (context.Request.Headers.Count > options.MaxRequestHeaderCount)
        {
            return new(
                StatusCodes.Status431RequestHeaderFieldsTooLarge,
                "request.headers_too_large",
                "The request headers are too large");
        }

        int headerBytes = 0;
        foreach ((string name, Microsoft.Extensions.Primitives.StringValues values)
                 in context.Request.Headers)
        {
            headerBytes += Encoding.UTF8.GetByteCount(name);
            foreach (string? value in values)
            {
                headerBytes += Encoding.UTF8.GetByteCount(value ?? string.Empty);
            }

            if (headerBytes > options.MaxRequestHeaderBytes)
            {
                return new(
                    StatusCodes.Status431RequestHeaderFieldsTooLarge,
                    "request.headers_too_large",
                    "The request headers are too large");
            }
        }

        if (context.Request.ContentLength > options.MaxRequestBodyBytes)
        {
            return new(
                StatusCodes.Status413PayloadTooLarge,
                "request.body_too_large",
                "The request body is too large");
        }

        return null;
    }
}

internal sealed class SecurityCorsPolicy
{
    private static readonly string[] ExposedHeaders =
        ["ETag", "Location", "Retry-After", "X-Correlation-ID"];
    private readonly HashSet<string> _origins;
    private readonly HashSet<string> _methods;
    private readonly HashSet<string> _headers;

    internal SecurityCorsPolicy(SecurityCorsOptions options)
    {
        _origins = options.AllowedOrigins
            .Select(origin =>
            {
                _ = TryNormalizeOrigin(origin, out Uri? normalized);
                return normalized!.GetLeftPart(UriPartial.Authority);
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _methods = options.AllowedMethods
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _headers = options.AllowedHeaders
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    internal async Task<bool> ApplyAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("Origin", out var origins))
        {
            return true;
        }

        if (origins.Count != 1 ||
            !TryNormalizeOrigin(origins[0], out Uri? origin))
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "cors.origin_denied",
                "The cross-origin request is not allowed");
            return false;
        }

        if (IsSameOrigin(context.Request, origin!))
        {
            return true;
        }

        if (!_origins.Contains(origin!.GetLeftPart(UriPartial.Authority)))
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "cors.origin_denied",
                "The cross-origin request is not allowed");
            return false;
        }

        string method = context.Request.Method;
        Microsoft.Extensions.Primitives.StringValues requestedMethods = default;
        bool isPreflight = HttpMethods.IsOptions(method) &&
            context.Request.Headers.TryGetValue(
                "Access-Control-Request-Method",
                out requestedMethods);
        if (isPreflight)
        {
            if (requestedMethods.Count != 1 ||
                !_methods.Contains(requestedMethods[0]!))
            {
                await SecurityProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "cors.method_denied",
                    "The cross-origin method is not allowed");
                return false;
            }

            if (!RequestedHeadersAreAllowed(context))
            {
                await SecurityProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "cors.headers_denied",
                    "The cross-origin headers are not allowed");
                return false;
            }
        }
        else if (!_methods.Contains(method))
        {
            await SecurityProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "cors.method_denied",
                "The cross-origin method is not allowed");
            return false;
        }

        ApplyCorsHeaders(context, origin.GetLeftPart(UriPartial.Authority));
        if (!isPreflight)
        {
            return true;
        }

        context.Response.Headers["Access-Control-Allow-Methods"] =
            string.Join(", ", _methods.Order(StringComparer.Ordinal));
        context.Response.Headers["Access-Control-Allow-Headers"] =
            string.Join(", ", _headers.Order(StringComparer.Ordinal));
        context.Response.Headers["Access-Control-Max-Age"] = "600";
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return false;
    }

    internal static bool TryNormalizeOrigin(
        string? value,
        out Uri? origin)
    {
        origin = null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('*', StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp &&
             parsed.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.AbsolutePath != "/")
        {
            return false;
        }

        origin = parsed;
        return true;
    }

    private bool RequestedHeadersAreAllowed(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(
                "Access-Control-Request-Headers",
                out var requested))
        {
            return true;
        }

        return requested.Count == 1 &&
            requested[0]!
                .Split(
                    ',',
                    StringSplitOptions.TrimEntries |
                    StringSplitOptions.RemoveEmptyEntries)
                .All(_headers.Contains);
    }

    private static bool IsSameOrigin(HttpRequest request, Uri origin)
    {
        if (!request.Host.HasValue ||
            !Uri.TryCreate(
                $"{request.Scheme}://{request.Host.Value}",
                UriKind.Absolute,
                out Uri? requestOrigin))
        {
            return false;
        }

        if (string.Equals(
                requestOrigin.GetLeftPart(UriPartial.Authority),
                origin.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
                request.Scheme,
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                origin.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                request.Host.Host,
                origin.IdnHost,
                StringComparison.OrdinalIgnoreCase) &&
            (request.Host.Port ?? 443) == origin.Port;
    }

    private static void ApplyCorsHeaders(HttpContext context, string origin)
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
        context.Response.Headers["Access-Control-Expose-Headers"] =
            string.Join(", ", ExposedHeaders);
        context.Response.Headers.Append("Vary", "Origin");
    }
}

internal static class SecurityRequestClassifier
{
    internal static bool IsRateLimited(HttpContext context) =>
        !HttpMethods.IsOptions(context.Request.Method) &&
        (context.Request.Path.StartsWithSegments("/api") ||
         context.Request.Path.StartsWithSegments("/v1") ||
         context.Request.Path.StartsWithSegments("/delivery"));

    internal static string RateLimitPartition(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    internal static string LogTarget(HttpContext context)
    {
        string? endpoint = context.GetEndpoint()?.DisplayName;
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        if (context.Request.Path.StartsWithSegments("/api"))
        {
            return "/api/*";
        }

        if (context.Request.Path.StartsWithSegments("/v1"))
        {
            return "/v1/*";
        }

        if (context.Request.Path.StartsWithSegments("/media") ||
            context.Request.Path.StartsWithSegments("/delivery"))
        {
            return "/media/*";
        }

        if (context.Request.Path.StartsWithSegments("/health"))
        {
            return "/health/*";
        }

        return "unmatched";
    }
}

internal static class SecurityProblemWriter
{
    internal static Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title)
    {
        context.Response.ContentLength = null;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsync(
            JsonSerializer.Serialize(new
            {
                type = $"https://vistara.dev/problems/{code.Replace('.', '-')}",
                title,
                status,
                code,
                traceId = context.TraceIdentifier,
            }),
            context.RequestAborted);
    }
}

internal static class SecurityLog
{
    private static readonly Action<ILogger, string, string, int, Exception?>
        RequestCompletedMessage =
            LoggerMessage.Define<string, string, int>(
                LogLevel.Information,
                new EventId(7001, "SecurityRequestCompleted"),
                "HTTP {Method} {Target} completed {StatusCode}; " +
                "sensitive request data redacted");

    internal static void RequestCompleted(
        ILogger logger,
        string method,
        string target,
        int statusCode) =>
        RequestCompletedMessage(logger, method, target, statusCode, null);
}
