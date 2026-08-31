using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Media;
using Vistara.Application.Common;
using Vistara.Application.Sharing;
using Vistara.Auth.Delivery;
using Vistara.Auth.Sharing;
using Vistara.Contracts.Media;
using Vistara.Persistence;

namespace Vistara.Api.Features.Shares;

/// <summary>
/// The share-scoped media route. The share token in the path is the whole
/// credential an anonymous recipient presents, so the same path can be placed
/// straight into an image element by a public share page.
/// </summary>
public static class ShareRenditionRoute
{
    public const string Pattern =
        "/api/v1/public/shares/{publicToken}/assets/{assetId:guid}/renditions/{renditionId}";

    public static string Build(
        string publicToken,
        Guid assetId,
        string deliveryIdentifier) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"/api/v1/public/shares/{Uri.EscapeDataString(publicToken)}" +
            $"/assets/{assetId:D}" +
            $"/renditions/{Uri.EscapeDataString(deliveryIdentifier)}");
}

public static class ShareRenditionEndpoint
{
    /// <summary>
    /// Serves one captured rendition to an anonymous share recipient. The share
    /// service decides the token, session, password, lifecycle, and membership
    /// questions, the delivery grant authorization port decides the resource
    /// question against the live share, and only then are bytes resolved inside
    /// the share's own tenant scope.
    /// </summary>
    public static async Task GetAsync(
        HttpContext context,
        string publicToken,
        Guid assetId,
        string renditionId,
        ShareService service,
        ShareDeliveryGrantAuthorizationPort authorization,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(clock);

        ShareRenditionResult resolved = await service.ResolvePublicRenditionAsync(
            publicToken,
            ShareEndpoint.ReadSessionToken(context),
            assetId,
            renditionId,
            cancellationToken);
        if (resolved.Status == ShareRenditionStatus.Gone)
        {
            await MediaDeliveryEndpoint.WriteMediaProblemAsync(
                context,
                StatusCodes.Status410Gone,
                "share_gone",
                "The share is no longer available",
                cancellationToken);
            return;
        }

        if (resolved.Status != ShareRenditionStatus.Available ||
            resolved.Target is not { } target)
        {
            await WriteConcealedAsync(context, cancellationToken);
            return;
        }

        if (!await IsGrantedAsync(
                authorization,
                target,
                clock.UtcNow,
                cancellationToken))
        {
            await WriteConcealedAsync(context, cancellationToken);
            return;
        }

        MediaRenditionScope scope;
        try
        {
            scope = new MediaRenditionScope(
                target.TenantId,
                target.AssetId,
                Guid.Parse(target.DeliveryIdentifier));
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            await WriteConcealedAsync(context, cancellationToken);
            return;
        }

        // The validated share token is the tenant-scoped credential for these
        // bytes, but the request may already carry a signed-in identity from
        // another tenant, so delivery runs in its own scope established from the
        // share instead of mutating the caller's tenant context.
        await using AsyncServiceScope delivery = scopeFactory.CreateAsyncScope();
        delivery.ServiceProvider
            .GetRequiredService<IMutableTenantScope>()
            .Establish(target.TenantId);
        IMediaDeliveryApplicationPort application = delivery.ServiceProvider
            .GetRequiredService<IMediaDeliveryApplicationPort>();
        await MediaDeliveryEndpoint.DeliverRenditionAsync(
            context,
            () => application.ResolveAssetRenditionAsync(
                scope,
                cancellationToken),
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            cancellationToken);
    }

    /// <summary>
    /// Asks the share delivery grant port whether this share, at its current
    /// version, still authorizes this exact rendition. Revoking, expiring, or
    /// editing the share changes the answer on the very next request.
    /// </summary>
    private static async ValueTask<bool> IsGrantedAsync(
        ShareDeliveryGrantAuthorizationPort authorization,
        ShareRenditionTarget target,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        DeliveryGrantIssueRequest request;
        try
        {
            request = new DeliveryGrantIssueRequest(
                target.TenantId,
                new DeliveryGrantIdentity(null, target.ShareId, target.ShareVersion),
                new DeliveryGrantResource(
                    target.AssetId,
                    target.RevisionId,
                    DeliveryGrantRendition.Derivative(target.DeliveryIdentifier)),
                DeliveryGrantAccess.ReadDerivative,
                nowUtc,
                nowUtc.AddMinutes(1));
        }
        catch (ArgumentException)
        {
            return false;
        }

        DeliveryGrantAuthorizationDecision decision =
            await authorization.AuthorizeIssueAsync(request, cancellationToken);
        return decision.IsAuthorized;
    }

    private static Task WriteConcealedAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        MediaDeliveryEndpoint.WriteMediaProblemAsync(
            context,
            StatusCodes.Status404NotFound,
            "share_rendition_not_found",
            "The shared rendition was not found",
            cancellationToken);
}
