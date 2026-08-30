using Vistara.Application.Common;
using Vistara.Application.Sharing;
using Vistara.Auth.Delivery;

namespace Vistara.Auth.Sharing;

public sealed class ShareDeliveryGrantAuthorizationPort(
    IClock clock,
    IShareStore shareStore) : IDeliveryGrantAuthorizationPort
{
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IShareStore _shareStore =
        shareStore ?? throw new ArgumentNullException(nameof(shareStore));

    public ValueTask<DeliveryGrantAuthorizationDecision> AuthorizeIssueAsync(
        DeliveryGrantIssueRequest request,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(
            request.TenantId,
            request.Identity,
            request.Resource,
            request.Permission,
            cancellationToken);

    public ValueTask<DeliveryGrantAuthorizationDecision> RevalidateAsync(
        DeliveryGrantRecord grant,
        CancellationToken cancellationToken) =>
        AuthorizeAsync(
            grant.TenantId,
            grant.Identity,
            grant.Resource,
            grant.Permission,
            cancellationToken);

    private async ValueTask<DeliveryGrantAuthorizationDecision> AuthorizeAsync(
        Guid tenantId,
        DeliveryGrantIdentity identity,
        DeliveryGrantResource resource,
        DeliveryGrantAccess access,
        CancellationToken cancellationToken)
    {
        if (!identity.ShareId.HasValue ||
            !identity.ShareVersion.HasValue)
        {
            return DeliveryGrantAuthorizationDecision.Forbidden();
        }

        ShareRecord? share = await _shareStore.FindAsync(
            tenantId,
            identity.ShareId.Value,
            cancellationToken);
        if (share is null ||
            share.Version != identity.ShareVersion.Value ||
            share.StatusAt(_clock.UtcNow) != ShareLifecycleStatus.Active)
        {
            return DeliveryGrantAuthorizationDecision.Concealed();
        }

        ShareAssetSnapshot? asset = share.Assets.SingleOrDefault(asset =>
            asset.AssetId == resource.AssetId &&
            asset.RevisionId == resource.RevisionId);
        if (asset is null)
        {
            return DeliveryGrantAuthorizationDecision.Concealed();
        }

        bool allowed = access switch
        {
            DeliveryGrantAccess.ReadDerivative =>
                asset.Renditions.Any(rendition =>
                    rendition.DeliveryIdentifier ==
                        resource.Rendition.Identifier &&
                    share.Permissions.HasFlag(rendition.RequiredAccess)),
            DeliveryGrantAccess.ReadOriginal =>
                asset.Renditions.Any(rendition =>
                    rendition.DeliveryIdentifier ==
                        resource.Rendition.Identifier &&
                    rendition.RequiredAccess ==
                        ShareAccess.DownloadOriginal &&
                    share.Permissions.HasFlag(
                        ShareAccess.DownloadOriginal)),
            DeliveryGrantAccess.ReadMetadata =>
                share.MetadataExposure == ShareMetadataExposure.Basic,
            _ => false,
        };
        return allowed
            ? DeliveryGrantAuthorizationDecision.Authorized()
            : DeliveryGrantAuthorizationDecision.Forbidden();
    }
}
