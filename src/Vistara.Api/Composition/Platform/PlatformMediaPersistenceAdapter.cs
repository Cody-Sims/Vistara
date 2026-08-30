using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Vistara.Api.Features.Media;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Auth.Delivery;
using Vistara.Domain.Common;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Media;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformMediaDeliveryAuthorizationPort(
    IPlatformTenantContext tenantContext,
    RelationalMediaCatalogStore media,
    RelationalAuthenticationStore authentication,
    IDeliveryGrantStore grantStore,
    DeliveryGrantValidator grantValidator,
    IDeliveryGrantPepperProvider pepperProvider) :
    IMediaDeliveryAuthorizationPort
{
    public async ValueTask<MediaDeliveryAccess>
        AuthorizePrivateDerivativeAsync(
            HttpContext context,
            MediaDeliveryCredential? credential,
            CancellationToken cancellationToken)
    {
        string? grant = credential?.PlaintextToken;
        if (grant is null ||
            !TryGetPepperVersion(grant, out string pepperVersion) ||
            !pepperProvider.TryGetPepper(
            pepperVersion,
            out ReadOnlyMemory<byte> pepper))
        {
            return MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Concealed);
        }

        string digest = ComputeDigest(pepper.Span, grant);
        PersistedAuthenticationRoute? route =
            await authentication.FindDeliveryGrantRouteAsync(
                digest,
                cancellationToken);
        if (route is null)
        {
            return MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Concealed);
        }

        DeliveryGrantRecord? record = await grantStore.FindAsync(
            route.CredentialId,
            cancellationToken);
        if (record is null ||
            record.TenantId != route.TenantId ||
            (record.Identity.SubjectId ?? record.Identity.ShareId) !=
                route.PrincipalId ||
            record.Resource.Rendition.Kind !=
                DeliveryGrantRenditionKind.Derivative ||
            record.Permission != DeliveryGrantAccess.ReadDerivative ||
            !string.Equals(
                record.Resource.Rendition.Identifier,
                ReadDerivativeIdentifier(context),
                StringComparison.Ordinal))
        {
            return MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Concealed);
        }

        Result<ValidatedDeliveryGrant> validation =
            await grantValidator.ValidateAsync(
                new DeliveryGrantValidationRequest(
                    grant,
                    record.TenantId,
                    record.Identity,
                    record.Resource,
                    DeliveryGrantAccess.ReadDerivative),
                cancellationToken);
        return validation.TryGetValue(out ValidatedDeliveryGrant? validated)
            ? MediaDeliveryAccess.AuthorizedAsset(
                validated.TenantId,
                validated.Resource.AssetId)
            : MediaDeliveryAccess.Denied(
                validation.Error?.Code == DeliveryGrantErrors.Forbidden.Code
                    ? MediaDeliveryAccessStatus.Forbidden
                    : MediaDeliveryAccessStatus.Concealed);
    }

    public async ValueTask<MediaDeliveryAccess> AuthorizeOriginalAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Unauthenticated);
        }

        if (!PlatformPrincipalReader.TryRead(
                context.User,
                tenantContext,
                "assets.read",
                out Guid tenantId,
                out Guid userId))
        {
            return MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Forbidden);
        }

        return await media.CanReadAssetAsync(
            tenantId,
            userId,
            assetId,
            cancellationToken)
            ? MediaDeliveryAccess.AuthorizedAsset(tenantId, assetId)
            : MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Concealed);
    }

    private static bool TryGetPepperVersion(
        string? token,
        out string version)
    {
        version = string.Empty;
        const string prefix = "vdg_";
        if (string.IsNullOrWhiteSpace(token) ||
            token.Length > DeliveryGrantTokenLimits.MaximumPlaintextLength ||
            !token.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        int separator = token.IndexOf('_', prefix.Length);
        if (separator <= prefix.Length)
        {
            return false;
        }

        version = token[prefix.Length..separator];
        return version.Length is >= 2 and <= 8 &&
            version[0] == 'v' &&
            version.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static string ComputeDigest(
        ReadOnlySpan<byte> pepper,
        string token)
    {
        byte[] tokenBytes = Encoding.UTF8.GetBytes(token);
        byte[]? digest = null;
        try
        {
            digest = HMACSHA256.HashData(pepper, tokenBytes);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private static string ReadDerivativeIdentifier(HttpContext context) =>
        string.Concat(
            context.Request.RouteValues["pipeline"]?.ToString(),
            ":",
            context.Request.RouteValues["sourceHash"]?.ToString(),
            ":",
            context.Request.RouteValues["recipeHash"]?.ToString(),
            ".",
            context.Request.RouteValues["extension"]?.ToString());
}

internal sealed class PlatformMediaDeliveryApplicationPort(
    RelationalMediaCatalogStore media,
    IBlobStore blobStore) : IMediaDeliveryApplicationPort
{
    public async ValueTask<MediaDeliveryResult> ResolvePublicDerivativeAsync(
        MediaDerivativeRequest request,
        CancellationToken cancellationToken)
    {
        PersistedPublicDerivativeRoute? route =
            await media.ResolvePublicDerivativeRouteAsync(
                request.Pipeline,
                request.SourceHash,
                request.RecipeHash,
                request.Extension,
                cancellationToken);
        if (route is null)
        {
            return MediaDeliveryResult.NotFound();
        }

        PersistedDerivativeMedia? derivative = await media.GetDerivativeAsync(
            route.TenantId,
            route.RequestId,
            cancellationToken);
        return derivative is null || !derivative.IsPublic
            ? MediaDeliveryResult.NotFound()
            : await ResolveDerivativeAsync(derivative, cancellationToken);
    }

    public async ValueTask<MediaDeliveryResult> ResolvePrivateDerivativeAsync(
        MediaTenantScope scope,
        MediaDerivativeRequest request,
        CancellationToken cancellationToken)
    {
        PersistedDerivativeMedia? derivative = await media.FindDerivativeAsync(
            scope.TenantId,
            request.Pipeline,
            request.SourceHash,
            request.RecipeHash,
            request.Extension,
            cancellationToken);
        return derivative is null
            ? MediaDeliveryResult.NotFound()
            : await ResolveDerivativeAsync(derivative, cancellationToken);
    }

    public async ValueTask<MediaDeliveryResult> ResolveOriginalAsync(
        MediaAssetScope scope,
        CancellationToken cancellationToken)
    {
        PersistedMediaObject? persisted = await media.GetOriginalAsync(
            scope.TenantId,
            scope.AssetId,
            cancellationToken);
        if (persisted is null)
        {
            return MediaDeliveryResult.NotFound();
        }

        var source = new BlobMediaContentSource(
            blobStore,
            new BlobKey(persisted.StorageKey),
            persisted.ProviderVersion is null
                ? null
                : new BlobVersion(persisted.ProviderVersion));
        return MediaDeliveryResult.Ready(new MediaRepresentation(
            persisted.ContentLength,
            persisted.ContentType,
            persisted.Sha256,
            source,
            persisted.DownloadFileName));
    }

    private async ValueTask<MediaDeliveryResult> ResolveDerivativeAsync(
        PersistedDerivativeMedia persisted,
        CancellationToken cancellationToken)
    {
        var key = new BlobKey(persisted.StorageKey);
        BlobHead? head = await blobStore.HeadAsync(key, cancellationToken);
        if (head is null)
        {
            return persisted.State is "Queued" or "Processing"
                ? MediaDeliveryResult.Queued()
                : MediaDeliveryResult.NotFound();
        }

        string? sha256 = persisted.Sha256 ??
            head.Properties.Checksums
                .SingleOrDefault(
                    checksum =>
                        checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
                ?.Value;
        if (sha256 is null)
        {
            throw new InvalidOperationException(
                "A derivative representation must expose a SHA-256 digest.");
        }

        var source = new BlobMediaContentSource(
            blobStore,
            key,
            head.Identity.Version);
        return MediaDeliveryResult.Ready(new MediaRepresentation(
            persisted.ContentLength ?? head.Properties.ContentLength,
            persisted.ContentType ?? head.Properties.ContentType.Value,
            sha256,
            source));
    }

    private sealed class BlobMediaContentSource(
        IBlobStore blobStore,
        BlobKey key,
        BlobVersion? version) : IMediaContentSource
    {
        public async ValueTask<MediaReadHandle> OpenReadAsync(
            MediaByteRange? range,
            CancellationToken cancellationToken)
        {
            BlobRange? blobRange = range is null
                ? null
                : new BlobRange(range.Offset, range.Length);
            BlobRequestConditions conditions = version is null
                ? BlobRequestConditions.None
                : new BlobRequestConditions(ifMatch: version);
            Vistara.Application.Common.Storage.BlobReadHandle handle =
                await blobStore.OpenReadAsync(
                    key,
                    new BlobReadOptions(blobRange, conditions),
                    cancellationToken);
            return new MediaReadHandle(handle.Content);
        }
    }
}

internal sealed class PlatformDeliveryGrantAuthorizationPort(
    RelationalMediaCatalogStore media,
    IClock clock) : IDeliveryGrantAuthorizationPort
{
    public ValueTask<DeliveryGrantAuthorizationDecision> AuthorizeIssueAsync(
        DeliveryGrantIssueRequest request,
        CancellationToken cancellationToken) =>
        RevalidateAsync(
            new DeliveryGrantRecord(
                Guid.CreateVersion7(),
                1,
                request.TenantId,
                request.Identity,
                request.Resource,
                request.Permission,
                clock.UtcNow,
                request.NotBeforeUtc,
                request.ExpiresAtUtc,
                "v0",
                new string('0', 64)),
            cancellationToken);

    public async ValueTask<DeliveryGrantAuthorizationDecision> RevalidateAsync(
        DeliveryGrantRecord grant,
        CancellationToken cancellationToken)
    {
        bool authorized = true;
        if (grant.Identity.SubjectId is { } subjectId)
        {
            authorized &= await media.RevalidateSubjectGrantAsync(
                grant.TenantId,
                subjectId,
                grant.Resource.AssetId,
                grant.Resource.RevisionId,
                cancellationToken);
        }

        if (grant.Identity.ShareId is { } shareId &&
            grant.Identity.ShareVersion is { } shareVersion)
        {
            authorized &= await media.RevalidateShareGrantAsync(
                grant.TenantId,
                shareId,
                shareVersion,
                grant.Resource.AssetId,
                grant.Resource.RevisionId,
                clock.UtcNow,
                cancellationToken);
        }

        return authorized
            ? DeliveryGrantAuthorizationDecision.Authorized()
            : DeliveryGrantAuthorizationDecision.Concealed();
    }
}

internal static class PlatformPrincipalReader
{
    internal static bool TryRead(
        ClaimsPrincipal principal,
        IPlatformTenantContext tenantContext,
        string requiredScope,
        out Guid tenantId,
        out Guid userId)
    {
        tenantId = default;
        userId = default;
        return principal.Identity?.IsAuthenticated == true &&
            principal.HasClaim("scope", requiredScope) &&
            TryReadUuid7(principal, "tenant_id", out tenantId) &&
            TryReadUuid7(
                principal,
                ClaimTypes.NameIdentifier,
                out userId) &&
            tenantContext.TenantId == tenantId;
    }

    private static bool TryReadUuid7(
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
