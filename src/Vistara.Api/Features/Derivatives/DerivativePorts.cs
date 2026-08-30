using Microsoft.AspNetCore.Http;
using Vistara.Contracts.Derivatives;
using Vistara.Contracts.Idempotency;

namespace Vistara.Api.Features.Derivatives;

public enum DerivativeAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed record DerivativeAccess
{
    private DerivativeAccess(
        DerivativeAccessStatus status,
        Guid? tenantId,
        Guid? assetId)
    {
        Status = status;
        TenantId = tenantId;
        AssetId = assetId;
    }

    public DerivativeAccessStatus Status { get; }

    public Guid? TenantId { get; }

    public Guid? AssetId { get; }

    public static DerivativeAccess AuthorizedCatalog(Guid tenantId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        return new DerivativeAccess(DerivativeAccessStatus.Authorized, tenantId, null);
    }

    public static DerivativeAccess AuthorizedAsset(Guid tenantId, Guid assetId)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(assetId, nameof(assetId));
        return new DerivativeAccess(
            DerivativeAccessStatus.Authorized,
            tenantId,
            assetId);
    }

    public static DerivativeAccess Denied(DerivativeAccessStatus status)
    {
        if (status == DerivativeAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new DerivativeAccess(status, null, null);
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("The identifier must be a UUIDv7 value.", parameterName);
        }
    }
}

public interface IDerivativeAuthorizationPort
{
    ValueTask<DerivativeAccess> AuthorizeCatalogAsync(
        HttpContext context,
        CancellationToken cancellationToken);

    ValueTask<DerivativeAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken);
}

public sealed record DerivativeAssetScope
{
    public DerivativeAssetScope(Guid tenantId, Guid assetId)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("The tenant ID must be UUIDv7.", nameof(tenantId));
        }

        if (assetId == Guid.Empty || assetId.Version != 7)
        {
            throw new ArgumentException("The asset ID must be UUIDv7.", nameof(assetId));
        }

        TenantId = tenantId;
        AssetId = assetId;
    }

    public Guid TenantId { get; }

    public Guid AssetId { get; }
}

public sealed record DerivativePresetDefinition(
    string Name,
    int ActiveRevision,
    IReadOnlyList<DerivativePresetRevisionDefinition> Revisions);

public sealed record DerivativePresetRevisionDefinition(
    int Revision,
    bool IsActive,
    DerivativeParameterBounds Parameters);

public sealed record DerivativeParameterBounds(
    int? MinimumWidth,
    int? MaximumWidth,
    int? MinimumHeight,
    int? MaximumHeight,
    int? MinimumQuality,
    int? MaximumQuality,
    IReadOnlyList<string> Fits,
    IReadOnlyList<string> Formats);

public sealed record CanonicalDerivativeParameters(
    int Width,
    int Height,
    string Fit,
    string Format,
    int Quality,
    DerivativeFocalPointContract? FocalPoint,
    DerivativeCropRectangleContract? Crop);

public sealed record CanonicalDerivativeRequest(
    string Preset,
    int Revision,
    CanonicalDerivativeParameters Parameters,
    string RequestHash);

public enum DerivativeCanonicalizationStatus
{
    Accepted,
    PresetNotFound,
    RevisionNotActive,
    ParametersNotAllowed,
}

public sealed record DerivativeCanonicalizationResult
{
    private DerivativeCanonicalizationResult(
        DerivativeCanonicalizationStatus status,
        CanonicalDerivativeRequest? request)
    {
        Status = status;
        Request = request;
    }

    public DerivativeCanonicalizationStatus Status { get; }

    public CanonicalDerivativeRequest? Request { get; }

    public static DerivativeCanonicalizationResult Accepted(
        CanonicalDerivativeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(DerivativeCanonicalizationStatus.Accepted, request);
    }

    public static DerivativeCanonicalizationResult Rejected(
        DerivativeCanonicalizationStatus status)
    {
        if (status == DerivativeCanonicalizationStatus.Accepted ||
            !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, null);
    }
}

public enum DerivativeWorkState
{
    Queued,
    Processing,
    Ready,
    Failed,
}

public sealed record DerivativeReadyRepresentation(
    int Width,
    int Height,
    string Format,
    string ContentType,
    string EntityTag);

public sealed record DerivativeWorkSnapshot(
    Guid RequestId,
    string Preset,
    int Revision,
    CanonicalDerivativeParameters Parameters,
    DerivativeWorkState State,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DerivativeReadyRepresentation? Representation,
    string? FailureCode);

public enum DerivativeSubmissionStatus
{
    Accepted,
    Ready,
    IdempotencyConflict,
}

public sealed record DerivativeSubmissionResult
{
    private DerivativeSubmissionResult(
        DerivativeSubmissionStatus status,
        DerivativeWorkSnapshot? snapshot,
        bool reusedExisting,
        bool replayed)
    {
        Status = status;
        Snapshot = snapshot;
        ReusedExisting = reusedExisting;
        Replayed = replayed;
    }

    public DerivativeSubmissionStatus Status { get; }

    public DerivativeWorkSnapshot? Snapshot { get; }

    public bool ReusedExisting { get; }

    public bool Replayed { get; init; }

    public static DerivativeSubmissionResult Accepted(
        DerivativeWorkSnapshot snapshot,
        bool reusedExisting = false,
        bool replayed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            DerivativeSubmissionStatus.Accepted,
            snapshot,
            reusedExisting,
            replayed);
    }

    public static DerivativeSubmissionResult Ready(
        DerivativeWorkSnapshot snapshot,
        bool reusedExisting = false,
        bool replayed = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(
            DerivativeSubmissionStatus.Ready,
            snapshot,
            reusedExisting,
            replayed);
    }

    public static DerivativeSubmissionResult Conflict() =>
        new(DerivativeSubmissionStatus.IdempotencyConflict, null, false, false);
}

public interface IDerivativeApplicationPort
{
    ValueTask<IReadOnlyList<DerivativePresetDefinition>> ListPresetsAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<DerivativeCanonicalizationResult> CanonicalizeAsync(
        DerivativeAssetScope scope,
        DerivativeRequestContract request,
        CancellationToken cancellationToken);

    ValueTask<DerivativeSubmissionResult> RequestAsync(
        DerivativeAssetScope scope,
        CanonicalDerivativeRequest request,
        IdempotencyKey idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<DerivativeWorkSnapshot>> ListAsync(
        DerivativeAssetScope scope,
        CancellationToken cancellationToken);

    ValueTask<DerivativeWorkSnapshot?> GetStatusAsync(
        DerivativeAssetScope scope,
        Guid requestId,
        CancellationToken cancellationToken);
}
