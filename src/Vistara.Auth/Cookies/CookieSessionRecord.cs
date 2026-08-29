using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Cookies;

public sealed record CookieSessionRecord
{
    public CookieSessionRecord(
        AuthSessionId id,
        string sessionTokenDigest,
        string antiforgeryTokenDigest,
        UserId userId,
        TenantId? tenantId,
        TenantRole? role,
        long userVersion,
        long? membershipVersion,
        DateTimeOffset issuedAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset absoluteExpiresAt,
        DateTimeOffset? revokedAt = null,
        long version = 1)
    {
        if (!IsSha256Digest(sessionTokenDigest) ||
            !IsSha256Digest(antiforgeryTokenDigest))
        {
            throw new ArgumentException("Cookie session token digests must be SHA-256 hashes.");
        }

        if (issuedAt.Offset != TimeSpan.Zero ||
            lastSeenAt.Offset != TimeSpan.Zero ||
            idleExpiresAt.Offset != TimeSpan.Zero ||
            absoluteExpiresAt.Offset != TimeSpan.Zero ||
            (revokedAt.HasValue && revokedAt.Value.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("Cookie session timestamps must use UTC.");
        }

        if (lastSeenAt < issuedAt ||
            idleExpiresAt <= lastSeenAt ||
            absoluteExpiresAt <= issuedAt ||
            idleExpiresAt > absoluteExpiresAt ||
            userVersion < 1 ||
            membershipVersion < 1 ||
            version < 1 ||
            (tenantId is null) != (role is null) ||
            (tenantId is null) != (membershipVersion is null))
        {
            throw new ArgumentException("The cookie session record is invalid.");
        }

        Id = id;
        SessionTokenDigest = sessionTokenDigest;
        AntiforgeryTokenDigest = antiforgeryTokenDigest;
        UserId = userId;
        TenantId = tenantId;
        Role = role;
        UserVersion = userVersion;
        MembershipVersion = membershipVersion;
        IssuedAt = issuedAt;
        LastSeenAt = lastSeenAt;
        IdleExpiresAt = idleExpiresAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        RevokedAt = revokedAt;
        Version = version;
    }

    public AuthSessionId Id { get; init; }

    public string SessionTokenDigest { get; init; }

    public string AntiforgeryTokenDigest { get; init; }

    public UserId UserId { get; init; }

    public TenantId? TenantId { get; init; }

    public TenantRole? Role { get; init; }

    public long UserVersion { get; init; }

    public long? MembershipVersion { get; init; }

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public DateTimeOffset IdleExpiresAt { get; init; }

    public DateTimeOffset AbsoluteExpiresAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public long Version { get; init; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null &&
        now < IdleExpiresAt &&
        now < AbsoluteExpiresAt;

    public override string ToString() => "[CookieSessionRecord REDACTED]";

    private static bool IsSha256Digest(string? value) =>
        value is { Length: 64 } &&
        value.All(character =>
            char.IsAsciiDigit(character) ||
            character is >= 'a' and <= 'f');
}
