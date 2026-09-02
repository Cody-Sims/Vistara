using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Features.Derivatives;
using Vistara.Api.Features.Events;
using Vistara.Api.Features.Media;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Delivery;
using Vistara.Auth.Jwt;

namespace Vistara.Api.Composition.Platform;

public static class PlatformUploadServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiUploads(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(options =>
            options.AddPolicy(
                UploadEndpointMapping.UploadPolicyName,
                policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim("scope", "assets.upload")));
        services.TryAddScoped<
            IUploadAuthorizationPort,
            PlatformUploadAuthorizationPort>();
        services.TryAddScoped<
            IUploadApplicationPort,
            MissingUploadApplicationPort>();
        return services;
    }

    internal sealed class MissingUploadApplicationPort : IUploadApplicationPort
    {
        public MissingUploadApplicationPort()
        {
            throw new InvalidOperationException(
                "No production implementation of IUploadApplicationPort is registered. " +
                "Register a persistence-backed upload application adapter before " +
                "validating the API composition.");
        }

        public ValueTask<UploadProviderPolicy> GetProviderPolicyAsync(
            Guid tenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadReserveResult> ReserveAsync(
            ReserveUploadRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadIssuance> IssueAsync(
            UploadSessionSnapshot session,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadSessionSnapshot?> GetAsync(
            Guid tenantId,
            Guid uploadId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadWriteResult> WriteProxyAsync(
            UploadSessionSnapshot session,
            Stream content,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadPartPlanResult> RefreshPartPlansAsync(
            UploadSessionSnapshot session,
            IReadOnlyList<int> partNumbers,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadCommitResult> CommitAsync(
            UploadSessionSnapshot session,
            IReadOnlyList<CommittedUploadPart> parts,
            Vistara.Contracts.Idempotency.IdempotencyKey idempotencyKey,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<UploadAbortResult> AbortAsync(
            UploadSessionSnapshot session,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    public static IServiceProvider ValidateVistaraApiPlatformComposition(
        this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ValidateVistaraApiOidcComposition();
        using IServiceScope scope = services.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IUploadAuthorizationPort>();
        _ = scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        _ = scope.ServiceProvider.GetRequiredService<IClock>();
        _ = scope.ServiceProvider.GetRequiredService<IUuid7Generator>();
        IServiceProviderIsService availability =
            services.GetRequiredService<IServiceProviderIsService>();
        if (!availability.IsService(typeof(IBlobStore)) ||
            !availability.IsService(typeof(IImageProcessor)))
        {
            return services;
        }

        Require<ICookieSessionStore>(availability);
        Require<ICookieAuthAuditSink>(availability);
        Require<IApiKeyStore>(availability);
        Require<IApiKeyAuditSink>(availability);
        Require<IJwtTenantMembershipProvider>(availability);
        Require<IJwtRevocationStore>(availability);
        Require<IJwtMetadataSigningKeyResolver>(availability);
        Require<IDeliveryGrantStore>(availability);
        Require<IDeliveryGrantAuthorizationPort>(availability);
        Require<IDeliveryGrantAuditSink>(availability);
        Require<IMediaDeliveryAuthorizationPort>(availability);
        Require<IMediaDeliveryApplicationPort>(availability);
        Require<IDerivativeAuthorizationPort>(availability);
        Require<IDerivativeApplicationPort>(availability);
        Require<IEventStreamAuthorizationPort>(availability);
        Require<IEventStreamSource>(availability);
        _ = scope.ServiceProvider
            .GetRequiredService<IPlatformCookieAuthenticator>();
        _ = scope.ServiceProvider
            .GetRequiredService<IPlatformApiKeyAuthenticator>();
        _ = scope.ServiceProvider
            .GetRequiredService<IPlatformBearerAuthenticator>();
        _ = scope.ServiceProvider.GetRequiredService<PlatformLoginSessionFactory>();
        _ = scope.ServiceProvider.GetRequiredService<ApiKeyAuthenticator>();
        _ = scope.ServiceProvider.GetRequiredService<JwtAuthenticator>();
        _ = scope.ServiceProvider
            .GetRequiredService<IMediaDeliveryAuthorizationPort>();
        _ = scope.ServiceProvider
            .GetRequiredService<IMediaDeliveryApplicationPort>();
        _ = scope.ServiceProvider
            .GetRequiredService<IDerivativeAuthorizationPort>();
        _ = scope.ServiceProvider
            .GetRequiredService<IDerivativeApplicationPort>();
        _ = scope.ServiceProvider
            .GetRequiredService<IEventStreamAuthorizationPort>();
        _ = scope.ServiceProvider.GetRequiredService<IEventStreamSource>();
        return services;
    }

    private static void Require<T>(IServiceProviderIsService availability)
    {
        if (!availability.IsService(typeof(T)))
        {
            throw new InvalidOperationException(
                $"No production implementation of {typeof(T).Name} is registered.");
        }
    }
}

internal sealed class PlatformUploadAuthorizationPort(
    IPlatformTenantContext tenantContext) : IUploadAuthorizationPort
{
    private readonly IPlatformTenantContext _tenantContext =
        tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));

    public ValueTask<UploadAccess> AuthorizeCreateAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Authorize(context, null, cancellationToken));

    public ValueTask<UploadAccess> AuthorizeSessionAsync(
        HttpContext context,
        Guid uploadId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Authorize(context, uploadId, cancellationToken));

    private UploadAccess Authorize(
        HttpContext context,
        Guid? uploadId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return UploadAccess.Denied(UploadAccessStatus.Unauthenticated);
        }

        if (uploadId is { } id && (id == Guid.Empty || id.Version != 7))
        {
            return UploadAccess.Denied(UploadAccessStatus.Concealed);
        }

        if (!context.User.HasClaim("scope", "assets.upload") ||
            !TryReadUuid7Claim(
                context.User,
                ClaimTypes.NameIdentifier,
                out Guid actorId) ||
            !TryReadUuid7Claim(context.User, "tenant_id", out Guid tenantId) ||
            _tenantContext.TenantId != tenantId)
        {
            return UploadAccess.Denied(UploadAccessStatus.Forbidden);
        }

        return UploadAccess.Authorized(tenantId, actorId);
    }

    private static bool TryReadUuid7Claim(
        ClaimsPrincipal principal,
        string claimType,
        out Guid value)
    {
        value = default;
        string[] values = principal.FindAll(claimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return values.Length == 1 &&
            Guid.TryParse(values[0], out value) &&
            value != Guid.Empty &&
            value.Version == 7;
    }
}
