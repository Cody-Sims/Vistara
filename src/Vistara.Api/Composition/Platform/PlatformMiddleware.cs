using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Composition.Runtime;
using Vistara.Auth.Cookies;
using Vistara.Persistence;

namespace Vistara.Api.Composition.Platform;

public interface IPlatformTenantContext
{
    Guid? TenantId { get; }
}

internal sealed class PlatformTenantContext :
    IPlatformTenantContext,
    IMutableTenantScope
{
    public Guid? TenantId { get; private set; }

    Guid ITenantScope.TenantId => TenantId ?? Guid.Empty;

    internal void Establish(Guid tenantId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new InvalidOperationException(
                "Authenticated tenant context must be a UUIDv7 value.");
        }

        if (TenantId.HasValue && TenantId.Value != tenantId)
        {
            throw new InvalidOperationException(
                "Tenant context cannot change during a request.");
        }

        TenantId = tenantId;
    }

    void IMutableTenantScope.Establish(Guid tenantId) => Establish(tenantId);
}

public sealed record PlatformRateLimitDecision(
    bool IsAllowed,
    TimeSpan? RetryAfter = null)
{
    public static PlatformRateLimitDecision Allow() => new(true);

    public static PlatformRateLimitDecision Reject(TimeSpan? retryAfter = null) =>
        new(false, retryAfter);
}

public interface IPlatformRateLimitHook
{
    ValueTask<PlatformRateLimitDecision> CheckAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}

internal sealed class PermitAllPlatformRateLimitHook : IPlatformRateLimitHook
{
    public ValueTask<PlatformRateLimitDecision> CheckAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(PlatformRateLimitDecision.Allow());
    }
}

internal static class PlatformProblemWriter
{
    internal static Task WriteAsync(
        HttpContext context,
        int status,
        string code,
        string title,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return Task.CompletedTask;
        }

        context.Response.ContentLength = null;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Accept-Ranges");
        context.Response.Headers.Remove("Content-Range");
        context.Response.Headers.Remove("Content-Disposition");
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
            cancellationToken);
    }
}

public static class PlatformApplicationBuilderExtensions
{
    public static IApplicationBuilder UseVistaraPlatform(
        this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<PlatformExceptionMiddleware>();
        app.UseMiddleware<PlatformCorrelationMiddleware>();
        app.UseVistaraApiRuntime();
        app.UseMiddleware<PlatformRateLimitMiddleware>();
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<PlatformTenantContextMiddleware>();
        app.UseMiddleware<PlatformAntiforgeryMiddleware>();
        app.UseAuthorization();
#pragma warning disable ASP0014
        app.UseEndpoints(static _ => { });
#pragma warning restore ASP0014
        return app;
    }

    public static IApplicationBuilder UseVistaraSpaFallback(
        this IApplicationBuilder app,
        RequestDelegate? spaFallback = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        bool hasCustomFallback = spaFallback is not null;
        RequestDelegate fallback = spaFallback ?? (static context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });

        return app.Use(async (context, next) =>
        {
            await next(context);
            if (context.Response.HasStarted ||
                context.Response.StatusCode != StatusCodes.Status404NotFound ||
                !HttpMethods.IsGet(context.Request.Method) ||
                IsReservedPath(context.Request.Path))
            {
                return;
            }

            if (hasCustomFallback)
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
            }

            await fallback(context);
        });
    }

    private static bool IsReservedPath(PathString path) =>
        path.StartsWithSegments("/v1") ||
        path.StartsWithSegments("/api/v1") ||
        path.StartsWithSegments("/media") ||
        path.StartsWithSegments("/delivery") ||
        path.StartsWithSegments("/health");
}

internal sealed class PlatformExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            await PlatformProblemWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "platform.unhandled_error",
                "The request could not be completed",
                context.RequestAborted);
        }
#pragma warning restore CA1031
    }
}

internal sealed class PlatformCorrelationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId =
            context.Request.Headers.TryGetValue("X-Correlation-ID", out var values) &&
            values.Count == 1 &&
            Guid.TryParse(values[0], out Guid supplied) &&
            supplied != Guid.Empty
                ? supplied.ToString("D")
                : Guid.CreateVersion7().ToString("D");
        context.TraceIdentifier = correlationId;
        context.Response.Headers["X-Correlation-ID"] = correlationId;
        await next(context);
    }
}

internal sealed class PlatformRateLimitMiddleware(
    RequestDelegate next,
    IPlatformRateLimitHook hook)
{
    public async Task InvokeAsync(HttpContext context)
    {
        PlatformRateLimitDecision decision = await hook.CheckAsync(
            context,
            context.RequestAborted);
        if (decision.IsAllowed)
        {
            await next(context);
            return;
        }

        if (decision.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        await PlatformProblemWriter.WriteAsync(
            context,
            StatusCodes.Status429TooManyRequests,
            "rate_limit.exceeded",
            "Too many requests",
            context.RequestAborted);
    }
}

internal sealed class PlatformTenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PlatformTenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            string[] tenantClaims = context.User.FindAll("tenant_id")
                .Select(claim => claim.Value)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (tenantClaims.Length != 1 ||
                !Guid.TryParse(tenantClaims[0], out Guid tenantId) ||
                tenantId.Version != 7)
            {
                await PlatformProblemWriter.WriteAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "tenancy.invalid_context",
                    "The authenticated tenant context is invalid",
                    context.RequestAborted);
                return;
            }

            tenantContext.Establish(tenantId);
        }

        await next(context);
    }
}

internal sealed class PlatformAntiforgeryMiddleware(
    RequestDelegate next,
    CookieAntiforgeryPolicy policy,
    CookieAuthOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        PlatformAuthenticationKind kind =
            context.Items.TryGetValue(PlatformAuthenticationState.KindKey, out object? value) &&
            value is PlatformAuthenticationKind authenticationKind
                ? authenticationKind
                : PlatformAuthenticationKind.Bearer;
        BrowserAuthenticationKind browserKind = kind switch
        {
            PlatformAuthenticationKind.Cookie => BrowserAuthenticationKind.Cookie,
            PlatformAuthenticationKind.ApiKey => BrowserAuthenticationKind.ApiKey,
            _ => BrowserAuthenticationKind.Bearer,
        };
        string? expectedDigest =
            context.Items.TryGetValue(
                PlatformAuthenticationState.AntiforgeryDigestKey,
                out object? digest)
                ? digest as string
                : null;
        Dictionary<string, string?> headers = context.Request.Headers
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Count == 1 ? pair.Value[0] : null,
                StringComparer.OrdinalIgnoreCase);
        AntiforgeryDecision decision = policy.Validate(
            context.Request.Method,
            browserKind,
            headers,
            expectedDigest,
            options);
        if (!decision.IsAllowed)
        {
            await PlatformProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                decision.Error!.Code,
                "A valid antiforgery token is required",
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
