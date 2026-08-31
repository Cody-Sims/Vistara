using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Auth.ApiKeys;

namespace Vistara.Api.Features.ApiKeys;

public static class ApiKeyServiceCollectionExtensions
{
    /// <summary>
    /// Registers API key administration on top of the existing issuer,
    /// revoker, store, and repository. All registrations use try-add
    /// semantics so a composition root may substitute any port.
    /// </summary>
    public static IServiceCollection AddVistaraApiKeyAdministration(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IAccountAuthorizationPort, ClaimsAccountAuthorizationPort>();
        services.TryAddSingleton<IApiKeyRandomSource, CryptographicApiKeyRandomSource>();
        services.TryAddScoped<ApiKeyIssuer>();
        services.TryAddScoped<ApiKeyRevoker>();
        services.TryAddScoped<
            IApiKeyAdministrationPort,
            PlatformApiKeyAdministrationAdapter>();
        return services;
    }
}

public static class ApiKeyEndpointMapping
{
    public const string PolicyName = "Vistara.ApiKeys";

    public static IEndpointRouteBuilder MapVistaraApiKeys(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        Map(endpoints.MapGet(
            "/api/v1/api-keys",
            (HttpContext context, CancellationToken cancellationToken) =>
                ApiKeyEndpoint.ListAsync(
                    context,
                    Authorization(context),
                    Administration(context),
                    cancellationToken)));
        Map(endpoints.MapPost(
            "/api/v1/api-keys",
            (HttpContext context, CancellationToken cancellationToken) =>
                ApiKeyEndpoint.CreateAsync(
                    context,
                    Authorization(context),
                    Administration(context),
                    cancellationToken)));
        Map(endpoints.MapDelete(
            "/api/v1/api-keys/{keyId:guid}",
            (
                Guid keyId,
                HttpContext context,
                CancellationToken cancellationToken) =>
                ApiKeyEndpoint.RevokeAsync(
                    context,
                    keyId,
                    Authorization(context),
                    Administration(context),
                    cancellationToken)));
        return endpoints;
    }

    private static void Map(RouteHandlerBuilder endpoint) =>
        endpoint.RequireAuthorization(PolicyName);

    private static IAccountAuthorizationPort Authorization(HttpContext context) =>
        context.RequestServices.GetRequiredService<IAccountAuthorizationPort>();

    private static IApiKeyAdministrationPort Administration(HttpContext context) =>
        context.RequestServices.GetRequiredService<IApiKeyAdministrationPort>();
}
