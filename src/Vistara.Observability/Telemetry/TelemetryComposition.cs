using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Vistara.Observability.Telemetry;

/// <summary>
/// Runtime telemetry configuration bound from the <c>Telemetry</c> section.
/// </summary>
public sealed class VistaraTelemetryOptions
{
    public const string SectionName = "Telemetry";

    public bool Enabled { get; set; } = true;

    public string ServiceName { get; set; } = string.Empty;

    public string? ServiceVersion { get; set; }

    public string? ServiceInstanceId { get; set; }

    public string? OtlpEndpoint { get; set; }

    public double SamplingRatio { get; set; } = 1.0;

    public bool Tracing { get; set; } = true;

    public bool Metrics { get; set; } = true;

    public bool Logging { get; set; } = true;

    internal Uri? ResolvedOtlpEndpoint { get; private set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServiceName))
        {
            throw new InvalidOperationException(
                "Telemetry requires a non-empty service name.");
        }

        if (double.IsNaN(SamplingRatio) || SamplingRatio is < 0 or > 1)
        {
            throw new InvalidOperationException(
                "Telemetry sampling ratio must be between 0 and 1.");
        }

        if (!string.IsNullOrWhiteSpace(OtlpEndpoint))
        {
            if (!Uri.TryCreate(OtlpEndpoint, UriKind.Absolute, out Uri? endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp &&
                    endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    "Telemetry OTLP endpoint must be an absolute http or https URI.");
            }

            ResolvedOtlpEndpoint = endpoint;
        }
    }
}

public static class VistaraTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing, metrics, and logging for the Vistara
    /// activity source and meter. Exporters stay disabled until an OTLP
    /// endpoint is configured so self-hosted installs make no outbound calls.
    /// </summary>
    public static IServiceCollection AddVistaraTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultServiceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        VistaraTelemetryOptions options = Read(
            configuration,
            defaultServiceName);
        services.TryAddSingleton(options);
        if (!options.Enabled)
        {
            return services;
        }

        IOpenTelemetryBuilder builder = services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: options.ServiceVersion,
                serviceInstanceId: options.ServiceInstanceId));

        if (options.Tracing)
        {
            builder.WithTracing(tracing =>
            {
                tracing.SetSampler(CreateSampler(options.SamplingRatio));
                tracing.AddSource(VistaraTelemetry.SourceName);
                if (options.ResolvedOtlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter(exporter =>
                        exporter.Endpoint = options.ResolvedOtlpEndpoint);
                }
            });
        }

        if (options.Metrics)
        {
            builder.WithMetrics(meters =>
            {
                meters.AddMeter(VistaraTelemetry.MeterName);
                meters.AddRuntimeInstrumentation();
                if (options.ResolvedOtlpEndpoint is not null)
                {
                    meters.AddOtlpExporter((exporter, _) =>
                        exporter.Endpoint = options.ResolvedOtlpEndpoint);
                }
            });
        }

        if (options.Logging)
        {
            services.Configure<OpenTelemetryLoggerOptions>(logger =>
            {
                logger.IncludeScopes = true;
                logger.IncludeFormattedMessage = true;
                logger.ParseStateValues = true;
            });
            builder.WithLogging(logging =>
            {
                if (options.ResolvedOtlpEndpoint is not null)
                {
                    logging.AddOtlpExporter(exporter =>
                        exporter.Endpoint = options.ResolvedOtlpEndpoint);
                }
            });
        }

        return services;
    }

    private static Sampler CreateSampler(double ratio) =>
        ratio >= 1
            ? new AlwaysOnSampler()
            : new ParentBasedSampler(new TraceIdRatioBasedSampler(ratio));

    private static VistaraTelemetryOptions Read(
        IConfiguration configuration,
        string defaultServiceName)
    {
        IConfigurationSection section =
            configuration.GetSection(VistaraTelemetryOptions.SectionName);
        var options = new VistaraTelemetryOptions
        {
            ServiceName = defaultServiceName,
        };
        section.Bind(options);
        options.Validate();
        return options;
    }
}
