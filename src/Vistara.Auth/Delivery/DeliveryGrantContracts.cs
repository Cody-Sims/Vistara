using Vistara.Domain.Common;

namespace Vistara.Auth.Delivery;

public enum DeliveryGrantAccess
{
    ReadDerivative,
    ReadOriginal,
    ReadMetadata,
}

public enum DeliveryGrantRenditionKind
{
    Derivative,
    Original,
    Metadata,
}

public sealed record DeliveryGrantRendition
{
    private const int MaximumIdentifierLength = 256;

    private DeliveryGrantRendition(
        DeliveryGrantRenditionKind kind,
        string identifier)
    {
        Kind = kind;
        Identifier = identifier;
    }

    public DeliveryGrantRenditionKind Kind { get; }

    public string Identifier { get; }

    public static DeliveryGrantRendition Derivative(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (identifier.Length > MaximumIdentifierLength ||
            identifier.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '_' or '.' or ':')))
        {
            throw new ArgumentException(
                "The derivative rendition identifier is invalid.",
                nameof(identifier));
        }

        return new(DeliveryGrantRenditionKind.Derivative, identifier);
    }

    public static DeliveryGrantRendition Original() =>
        new(DeliveryGrantRenditionKind.Original, "original");

    public static DeliveryGrantRendition Metadata() =>
        new(DeliveryGrantRenditionKind.Metadata, "metadata");
}

public sealed record DeliveryGrantIdentity
{
    public DeliveryGrantIdentity(
        Guid? subjectId,
        Guid? shareId,
        long? shareVersion)
    {
        if (subjectId.HasValue)
        {
            EnsureUuid7(subjectId.Value, nameof(subjectId));
        }

        if (shareId.HasValue)
        {
            EnsureUuid7(shareId.Value, nameof(shareId));
        }

        if (!subjectId.HasValue && !shareId.HasValue)
        {
            throw new ArgumentException(
                "A delivery grant must bind a subject or share.");
        }

        if (shareId.HasValue != shareVersion.HasValue ||
            shareVersion is < 1)
        {
            throw new ArgumentException(
                "Share-bound grants require a positive share version.",
                nameof(shareVersion));
        }

        SubjectId = subjectId;
        ShareId = shareId;
        ShareVersion = shareVersion;
    }

    public Guid? SubjectId { get; }

    public Guid? ShareId { get; }

    public long? ShareVersion { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Delivery grant identities must use UUIDv7 identifiers.",
                parameterName);
        }
    }
}

public sealed record DeliveryGrantResource
{
    public DeliveryGrantResource(
        Guid assetId,
        Guid revisionId,
        DeliveryGrantRendition rendition)
    {
        EnsureUuid7(assetId, nameof(assetId));
        EnsureUuid7(revisionId, nameof(revisionId));
        ArgumentNullException.ThrowIfNull(rendition);
        AssetId = assetId;
        RevisionId = revisionId;
        Rendition = rendition;
    }

    public Guid AssetId { get; }

    public Guid RevisionId { get; }

    public DeliveryGrantRendition Rendition { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Delivery grant resources must use UUIDv7 identifiers.",
                parameterName);
        }
    }
}

public sealed record DeliveryGrantIssueRequest
{
    public DeliveryGrantIssueRequest(
        Guid tenantId,
        DeliveryGrantIdentity identity,
        DeliveryGrantResource resource,
        DeliveryGrantAccess permission,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException(
                "The tenant identifier must be UUIDv7.",
                nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(resource);
        if (!Enum.IsDefined(permission))
        {
            throw new ArgumentOutOfRangeException(nameof(permission));
        }

        if (notBeforeUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc.Offset != TimeSpan.Zero ||
            expiresAtUtc <= notBeforeUtc)
        {
            throw new ArgumentException(
                "Delivery grant times must be UTC with expiry after not-before.");
        }

        ValidatePermission(permission, resource.Rendition.Kind);
        TenantId = tenantId;
        Identity = identity;
        Resource = resource;
        Permission = permission;
        NotBeforeUtc = notBeforeUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid TenantId { get; }

    public DeliveryGrantIdentity Identity { get; }

    public DeliveryGrantResource Resource { get; }

    public DeliveryGrantAccess Permission { get; }

    public DateTimeOffset NotBeforeUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    internal static void ValidatePermission(
        DeliveryGrantAccess permission,
        DeliveryGrantRenditionKind renditionKind)
    {
        bool valid = (permission, renditionKind) switch
        {
            (DeliveryGrantAccess.ReadDerivative, DeliveryGrantRenditionKind.Derivative) => true,
            (DeliveryGrantAccess.ReadOriginal, DeliveryGrantRenditionKind.Original) => true,
            (DeliveryGrantAccess.ReadMetadata, DeliveryGrantRenditionKind.Metadata) => true,
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException(
                "The delivery permission does not match the requested rendition.");
        }
    }
}

public sealed record DeliveryGrantRecord(
    Guid GrantId,
    long Version,
    Guid TenantId,
    DeliveryGrantIdentity Identity,
    DeliveryGrantResource Resource,
    DeliveryGrantAccess Permission,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string PepperVersionId,
    string TokenDigestHex,
    DateTimeOffset? RevokedAtUtc = null);

public sealed record IssuedDeliveryGrant(
    Guid GrantId,
    long Version,
    Guid TenantId,
    DeliveryGrantIdentity Identity,
    DeliveryGrantResource Resource,
    DeliveryGrantAccess Permission,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string PlaintextToken)
{
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"IssuedDeliveryGrant {{ GrantId = {GrantId}, Version = {Version}, " +
            $"TenantId = {TenantId}, Permission = {Permission}, " +
            $"ExpiresAtUtc = {ExpiresAtUtc}, PlaintextToken = " +
            $"{DeliveryGrantAuditEvent.RedactedPresentedToken} }}");
}

public sealed record DeliveryGrantValidationRequest(
    string? PlaintextToken,
    Guid TenantId,
    DeliveryGrantIdentity Identity,
    DeliveryGrantResource Resource,
    DeliveryGrantAccess RequiredAccess)
{
    public override string ToString() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"DeliveryGrantValidationRequest {{ PlaintextToken = " +
            $"{DeliveryGrantAuditEvent.RedactedPresentedToken}, " +
            $"TenantId = {TenantId}, Identity = {Identity}, " +
            $"Resource = {Resource}, RequiredAccess = {RequiredAccess} }}");
}

public sealed record PrivateDeliveryCachePolicy
{
    public PrivateDeliveryCachePolicy(TimeSpan maxAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maxAge,
            TimeSpan.Zero);

        MaxAge = maxAge;
        long seconds = Math.Max(0, (long)Math.Floor(maxAge.TotalSeconds));
        CacheControl = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"private, max-age={seconds}");
    }

    public bool IsPrivate { get; } = true;

    public TimeSpan MaxAge { get; }

    public string CacheControl { get; }
}

public sealed record ValidatedDeliveryGrant(
    Guid GrantId,
    long Version,
    Guid TenantId,
    DeliveryGrantIdentity Identity,
    DeliveryGrantResource Resource,
    DeliveryGrantAccess Access,
    DateTimeOffset ExpiresAtUtc,
    PrivateDeliveryCachePolicy CachePolicy);

public static class DeliveryGrantTokenLimits
{
    public const int MaximumPlaintextLength = 128;
}

public sealed class DeliveryGrantOptions
{
    public static DeliveryGrantOptions Default { get; } = new(
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(1));

    public DeliveryGrantOptions(
        TimeSpan maximumGrantTtl,
        TimeSpan maximumPrivateCacheTtl)
    {
        if (maximumGrantTtl <= TimeSpan.Zero ||
            maximumGrantTtl > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumGrantTtl));
        }

        if (maximumPrivateCacheTtl <= TimeSpan.Zero ||
            maximumPrivateCacheTtl > maximumGrantTtl)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPrivateCacheTtl));
        }

        MaximumGrantTtl = maximumGrantTtl;
        MaximumPrivateCacheTtl = maximumPrivateCacheTtl;
    }

    public TimeSpan MaximumGrantTtl { get; }

    public TimeSpan MaximumPrivateCacheTtl { get; }
}

public interface IDeliveryGrantStore
{
    ValueTask<Result> AddAsync(
        DeliveryGrantRecord grant,
        CancellationToken cancellationToken);

    ValueTask<DeliveryGrantRecord?> FindAsync(
        Guid grantId,
        CancellationToken cancellationToken);

    ValueTask<DeliveryGrantRecord?> RevokeAsync(
        Guid tenantId,
        Guid grantId,
        long expectedVersion,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken);
}

public interface IDeliveryGrantAuthorizationPort
{
    ValueTask<DeliveryGrantAuthorizationDecision> AuthorizeIssueAsync(
        DeliveryGrantIssueRequest request,
        CancellationToken cancellationToken);

    ValueTask<DeliveryGrantAuthorizationDecision> RevalidateAsync(
        DeliveryGrantRecord grant,
        CancellationToken cancellationToken);
}

public sealed record DeliveryGrantAuthorizationDecision
{
    private DeliveryGrantAuthorizationDecision(bool isAuthorized, bool isConcealed)
    {
        IsAuthorized = isAuthorized;
        IsConcealed = isConcealed;
    }

    public bool IsAuthorized { get; }

    public bool IsConcealed { get; }

    public static DeliveryGrantAuthorizationDecision Authorized() =>
        new(true, false);

    public static DeliveryGrantAuthorizationDecision Forbidden() =>
        new(false, false);

    public static DeliveryGrantAuthorizationDecision Concealed() =>
        new(false, true);
}

public interface IDeliveryGrantRandomSource
{
    void Fill(Span<byte> destination);
}

public interface IDeliveryGrantPepperProvider
{
    string CurrentVersionId { get; }

    bool TryGetPepper(string versionId, out ReadOnlyMemory<byte> pepper);
}

public interface IDeliveryGrantDigestComparer
{
    bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual);
}

public interface IDeliveryGrantAuditSink
{
    ValueTask WriteAsync(
        DeliveryGrantAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public enum DeliveryGrantAuditAction
{
    Issued,
    Validated,
    ValidationRejected,
    Revoked,
}

public sealed record DeliveryGrantAuditEvent
{
    public const string RedactedPresentedToken = "[REDACTED]";

    public DeliveryGrantAuditEvent(
        DeliveryGrantAuditAction action,
        Guid? tenantId,
        Guid? grantId,
        Guid? actorId,
        string? reasonCode,
        DateTimeOffset occurredAtUtc)
    {
        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Audit timestamps must use UTC.",
                nameof(occurredAtUtc));
        }

        Action = action;
        TenantId = tenantId;
        GrantId = grantId;
        ActorId = actorId;
        ReasonCode = reasonCode;
        OccurredAtUtc = occurredAtUtc;
    }

    public DeliveryGrantAuditAction Action { get; }

    public Guid? TenantId { get; }

    public Guid? GrantId { get; }

    public Guid? ActorId { get; }

    public string? ReasonCode { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string PresentedToken { get; } = RedactedPresentedToken;
}
