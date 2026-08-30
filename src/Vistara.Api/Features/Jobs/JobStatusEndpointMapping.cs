using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Application.Jobs;
using Vistara.Persistence.Jobs;

namespace Vistara.Api.Features.Jobs;

public static class JobStatusServiceCollectionExtensions
{
    /// <summary>
    /// Registers the tenant-scoped job status defaults using try-add semantics
    /// so a composition root may substitute either port.
    /// </summary>
    public static IServiceCollection AddVistaraJobStatus(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<
            IJobStatusAuthorizationPort,
            ClaimsJobStatusAuthorizationPort>();
        services.TryAddScoped<IJobStatusReader, RelationalJobStatusReader>();
        return services;
    }
}

public static class JobStatusEndpointMapping
{
    public const string PolicyName = "Vistara.Jobs";

    public static IEndpointRouteBuilder MapVistaraJobStatus(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
                "/api/v1/jobs/{jobId:guid}",
                (
                    Guid jobId,
                    HttpContext context,
                    CancellationToken cancellationToken) =>
                    JobStatusEndpoint.GetAsync(
                        context,
                        jobId,
                        context.RequestServices
                            .GetRequiredService<IJobStatusAuthorizationPort>(),
                        context.RequestServices
                            .GetRequiredService<IJobStatusReader>(),
                        cancellationToken))
            .RequireAuthorization(PolicyName);
        return endpoints;
    }
}
