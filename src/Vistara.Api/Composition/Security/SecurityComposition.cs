using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vistara.Api.Security;

namespace Vistara.Api.Composition.Security;

public interface IVistaraSecurityRegistration;

internal sealed class VistaraSecurityRegistration : IVistaraSecurityRegistration;

public sealed class VistaraSecurityOptions
{
    public const string SectionName = "Security";

    public SecurityCorsOptions Cors { get; set; } = new();
    public SecurityLimitOptions Limits { get; set; } = new();
    public List<string> RequiredSecretKeys { get; set; } = [];
}

public sealed class SecurityCorsOptions
{
    public List<string> AllowedOrigins { get; set; } = [];
    public List<string> AllowedMethods { get; set; } =
        ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"];
    public List<string> AllowedHeaders { get; set; } =
    [
        "Accept",
        "Authorization",
        "Content-Type",
        "If-Match",
        "If-None-Match",
        "Range",
        "X-API-Key",
        "X-Correlation-ID",
        "X-Vistara-CSRF",
    ];
}

public sealed class SecurityLimitOptions
{
    public long MaxRequestBodyBytes { get; set; } = 50L * 1024 * 1024;
    public int MaxRequestTargetBytes { get; set; } = 8 * 1024;
    public int MaxRequestHeaderBytes { get; set; } = 32 * 1024;
    public int MaxRequestHeaderCount { get; set; } = 100;
    public int RequestsPerWindow { get; set; } = 300;
    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromMinutes(1);
}

public static class SecurityServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiSecurity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IVistaraSecurityRegistration)))
        {
            return services;
        }

        services.AddSingleton<IVistaraSecurityRegistration,
            VistaraSecurityRegistration>();
        services.AddOptions<VistaraSecurityOptions>()
            .Bind(configuration.GetSection(VistaraSecurityOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<VistaraSecurityOptions>>(
                new VistaraSecurityOptionsValidator(
                    configuration,
                    environment)));
        services.TryAddEnumerable(
            ServiceDescriptor.Transient<IStartupFilter,
                VistaraSecurityStartupFilter>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IConfigureOptions<KestrelServerOptions>,
                VistaraKestrelSecurityOptions>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IPostConfigureOptions<LoggerFilterOptions>,
                VistaraSecurityLoggerFilterOptions>());
        services.Configure<RouteHandlerOptions>(
            options => options.ThrowOnBadRequest = false);
        services.AddSingleton(static provider =>
        {
            SecurityLimitOptions limits = provider
                .GetRequiredService<IOptions<VistaraSecurityOptions>>()
                .Value
                .Limits;
            return PartitionedRateLimiter.Create<HttpContext, string>(
                context => RateLimitPartition.GetFixedWindowLimiter(
                    SecurityRequestClassifier.RateLimitPartition(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = limits.RequestsPerWindow,
                        QueueLimit = 0,
                        Window = limits.RateLimitWindow,
                    }));
        });
        return services;
    }
}

public static class SecurityApplicationBuilderExtensions
{
    public static IApplicationBuilder UseVistaraApiSecurity(
        this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<VistaraSecurityMiddleware>();
    }
}

internal sealed class VistaraSecurityOptionsValidator(
    IConfiguration configuration,
    IHostEnvironment environment) :
    IValidateOptions<VistaraSecurityOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        VistaraSecurityOptions options)
    {
        List<string> failures = [];
        ValidateCors(options.Cors, environment, failures);
        ValidateLimits(options.Limits, failures);

        foreach (string key in options.RequiredSecretKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                failures.Add("Required secret configuration keys cannot be empty.");
            }
            else if (string.IsNullOrWhiteSpace(configuration[key]))
            {
                failures.Add($"Required secret configuration '{key}' is missing.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateCors(
        SecurityCorsOptions cors,
        IHostEnvironment environment,
        List<string> failures)
    {
        foreach (string origin in cors.AllowedOrigins)
        {
            if (!SecurityCorsPolicy.TryNormalizeOrigin(origin, out Uri? uri))
            {
                failures.Add($"CORS origin '{origin}' must be an absolute HTTP origin.");
                continue;
            }

            if (!environment.IsDevelopment() &&
                !string.Equals(uri!.Scheme, Uri.UriSchemeHttps,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    $"CORS origin '{origin}' must use HTTPS outside development.");
            }
        }

        if (cors.AllowedOrigins.Count !=
            cors.AllowedOrigins.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            failures.Add("CORS origins must be unique.");
        }

        if (cors.AllowedMethods.Count == 0 ||
            cors.AllowedMethods.Any(method =>
                string.IsNullOrWhiteSpace(method) ||
                method.Any(character => !char.IsAsciiLetter(character))))
        {
            failures.Add("CORS methods must contain valid HTTP method names.");
        }

        if (cors.AllowedHeaders.Any(header =>
                string.IsNullOrWhiteSpace(header) ||
                header.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-')))
        {
            failures.Add("CORS headers contain an invalid header name.");
        }
    }

    private static void ValidateLimits(
        SecurityLimitOptions limits,
        List<string> failures)
    {
        if (limits.MaxRequestBodyBytes is < 1 or > 1_073_741_824)
        {
            failures.Add("The request body limit must be between 1 byte and 1 GiB.");
        }

        if (limits.MaxRequestTargetBytes is < 256 or > 65_536)
        {
            failures.Add("The request target limit must be between 256 and 65536 bytes.");
        }

        if (limits.MaxRequestHeaderBytes is < 1024 or > 1_048_576)
        {
            failures.Add("The request header limit must be between 1024 bytes and 1 MiB.");
        }

        if (limits.MaxRequestHeaderCount is < 1 or > 1000)
        {
            failures.Add("The request header count limit must be between 1 and 1000.");
        }

        if (limits.RequestsPerWindow is < 1 or > 1_000_000)
        {
            failures.Add("The rate limit permit count must be between 1 and 1000000.");
        }

        if (limits.RateLimitWindow < TimeSpan.FromSeconds(1) ||
            limits.RateLimitWindow > TimeSpan.FromHours(1))
        {
            failures.Add("The rate limit window must be between 1 second and 1 hour.");
        }
    }
}

internal sealed class VistaraKestrelSecurityOptions(
    IOptions<VistaraSecurityOptions> securityOptions) :
    IConfigureOptions<KestrelServerOptions>
{
    public void Configure(KestrelServerOptions options)
    {
        SecurityLimitOptions limits = securityOptions.Value.Limits;
        options.Limits.MaxRequestBodySize = limits.MaxRequestBodyBytes;
        options.Limits.MaxRequestLineSize = limits.MaxRequestTargetBytes;
        options.Limits.MaxRequestHeadersTotalSize = limits.MaxRequestHeaderBytes;
        options.Limits.MaxRequestHeaderCount = limits.MaxRequestHeaderCount;
    }
}

internal sealed class VistaraSecurityLoggerFilterOptions :
    IPostConfigureOptions<LoggerFilterOptions>
{
    public void PostConfigure(string? name, LoggerFilterOptions options)
    {
        options.Rules.Add(
            new LoggerFilterRule(
                null,
                "Microsoft.AspNetCore.Hosting.Diagnostics",
                LogLevel.Warning,
                null));
    }
}

internal sealed class VistaraSecurityStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(
        Action<IApplicationBuilder> next) =>
        app =>
        {
            app.UseVistaraApiSecurity();
            next(app);
        };
}
