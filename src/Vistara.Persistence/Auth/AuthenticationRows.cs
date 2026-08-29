using Vistara.Persistence.Model;

namespace Vistara.Persistence.Auth;

internal static class AuthenticationRouteKinds
{
    internal const string ApiKey = "ApiKey";
    internal const string CookieSession = "CookieSession";
    internal const string DeliveryGrant = "DeliveryGrant";
}

internal sealed class AuthenticationRouteRow
{
    public string LookupDigest { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid RoutedTenantId { get; set; }
    public Guid PrincipalId { get; set; }
    public Guid CredentialId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CookieSessionRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public string SessionTokenDigest { get; set; } = string.Empty;
    public string AntiforgeryTokenDigest { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public long UserVersion { get; set; }
    public long MembershipVersion { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public long Version { get; set; }
}

internal sealed class DeliveryGrantRow : ITenantOwnedRow
{
    public Guid Id { get; set; }
    public TenantKey TenantId { get; set; }
    public Guid? SubjectId { get; set; }
    public Guid? ShareId { get; set; }
    public long? ShareVersion { get; set; }
    public Guid AssetId { get; set; }
    public Guid RevisionId { get; set; }
    public string RenditionKind { get; set; } = string.Empty;
    public string RenditionIdentifier { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public DateTimeOffset IssuedAtUtc { get; set; }
    public DateTimeOffset NotBeforeUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public string PepperVersionId { get; set; } = string.Empty;
    public string TokenDigestHex { get; set; } = string.Empty;
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public long Version { get; set; }
}
