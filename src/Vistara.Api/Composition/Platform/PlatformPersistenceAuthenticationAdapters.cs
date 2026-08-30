using Microsoft.Extensions.Logging;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Delivery;
using Vistara.Auth.Jwt;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Auth;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformApiKeyStore(RelationalAuthenticationStore store)
    : IApiKeyStore
{
    public async ValueTask<Result> AddAsync(
        ApiKeyMetadata metadata,
        CancellationToken cancellationToken) =>
        await store.AddApiKeyAsync(metadata, cancellationToken)
            ? Result.Success()
            : Result.Failure(ResultError.Conflict(
                "api_keys.already_exists",
                "The API key already exists."));

    public async ValueTask<ApiKeyAuthenticationRecord?>
        FindForAuthenticationAsync(
            ApiKeyId keyId,
            CancellationToken cancellationToken)
    {
        PersistedApiKeyAuthentication? persisted =
            await store.FindApiKeyForAuthenticationAsync(
                keyId.Value,
                cancellationToken);
        return persisted is null
            ? null
            : new ApiKeyAuthenticationRecord(
                persisted.Metadata,
                persisted.TenantStatus);
    }

    public async ValueTask<Result> RevokeAsync(
        TenantId tenantId,
        ApiKeyId keyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken) =>
        await store.RevokeApiKeyAsync(
            tenantId.Value,
            keyId.Value,
            revokedAt,
            cancellationToken) == AuthenticationMutationStatus.Applied
            ? Result.Success()
            : Result.Failure(ApiKeyErrors.NotFound);

    public ValueTask RecordLastUsedAsync(
        TenantId tenantId,
        ApiKeyId keyId,
        DateTimeOffset coarseUsedAt,
        CancellationToken cancellationToken) =>
        store.RecordApiKeyLastUsedAsync(
            tenantId.Value,
            keyId.Value,
            coarseUsedAt,
            cancellationToken);
}

internal sealed class PlatformCookieSessionStore(
    RelationalAuthenticationStore store) : ICookieSessionStore
{
    public async ValueTask<CookieSessionRecord?> FindAsync(
        string sessionTokenDigest,
        CancellationToken cancellationToken)
    {
        PersistedCookieSession? persisted =
            await store.FindCookieSessionAsync(
                sessionTokenDigest,
                cancellationToken);
        return persisted is null ? null : ToAuth(persisted);
    }

    public ValueTask<bool> AddAsync(
        CookieSessionRecord record,
        CancellationToken cancellationToken) =>
        record.TenantId is null ||
        record.Role is null ||
        record.MembershipVersion is null
            ? ValueTask.FromResult(false)
            : store.AddCookieSessionAsync(
                ToPersistence(record),
                cancellationToken);

    public ValueTask<bool> RotateAsync(
        string currentSessionTokenDigest,
        long expectedVersion,
        CookieSessionRecord replacement,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken) =>
        replacement.TenantId is null ||
        replacement.Role is null ||
        replacement.MembershipVersion is null
            ? ValueTask.FromResult(false)
            : store.RotateCookieSessionAsync(
                currentSessionTokenDigest,
                expectedVersion,
                ToPersistence(replacement),
                revokedAt,
                cancellationToken);

    public ValueTask RevokeAsync(
        string sessionTokenDigest,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken) =>
        store.RevokeCookieSessionAsync(
            sessionTokenDigest,
            revokedAt,
            cancellationToken);

    public ValueTask RevokeUserAsync(
        UserId userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken) =>
        store.RevokeUserCookieSessionsAsync(
            userId.Value,
            revokedAt,
            cancellationToken);

    public ValueTask RevokeMembershipAsync(
        UserId userId,
        TenantId tenantId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken) =>
        store.RevokeMembershipCookieSessionsAsync(
            userId.Value,
            tenantId.Value,
            revokedAt,
            cancellationToken);

    private static PersistedCookieSession ToPersistence(
        CookieSessionRecord record) =>
        new(
            record.Id.Value,
            record.SessionTokenDigest,
            record.AntiforgeryTokenDigest,
            record.UserId.Value,
            record.TenantId!.Value.Value,
            record.Role!.Value,
            record.UserVersion,
            record.MembershipVersion!.Value,
            record.IssuedAt,
            record.LastSeenAt,
            record.IdleExpiresAt,
            record.AbsoluteExpiresAt,
            record.RevokedAt,
            record.Version);

    private static CookieSessionRecord ToAuth(PersistedCookieSession record) =>
        new(
            new AuthSessionId(record.Id),
            record.SessionTokenDigest,
            record.AntiforgeryTokenDigest,
            new UserId(record.UserId),
            new TenantId(record.TenantId),
            record.Role,
            record.UserVersion,
            record.MembershipVersion,
            record.IssuedAtUtc,
            record.LastSeenAtUtc,
            record.IdleExpiresAtUtc,
            record.AbsoluteExpiresAtUtc,
            record.RevokedAtUtc,
            record.Version);
}

internal sealed class PlatformJwtTenantMembershipProvider(
    RelationalAuthenticationStore store) : IJwtTenantMembershipProvider
{
    public async ValueTask<JwtTenantMembership?> FindAsync(
        string issuer,
        string subject,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        PersistedJwtMembership? persisted =
            await store.FindJwtMembershipAsync(
                issuer,
                subject,
                tenantId.Value,
                cancellationToken);
        if (persisted is null ||
            !Enum.TryParse(persisted.TenantStatus, out TenantStatus tenantStatus) ||
            !Enum.TryParse(
                persisted.MembershipStatus,
                out MembershipStatus membershipStatus) ||
            !Enum.TryParse(persisted.Role, out TenantRole role))
        {
            return null;
        }

        return new JwtTenantMembership(
            new UserId(persisted.UserId),
            new TenantId(persisted.TenantId),
            tenantStatus,
            membershipStatus,
            role);
    }
}

internal sealed class PlatformJwtRevocationStore(
    RelationalAuthenticationStore store) : IJwtRevocationStore
{
    public ValueTask<bool> IsRevokedAsync(
        string issuer,
        string jwtId,
        CancellationToken cancellationToken) =>
        store.IsJwtRevokedAsync(issuer, jwtId, cancellationToken);
}

internal sealed class PlatformDeliveryGrantStore(
    RelationalAuthenticationStore store) : IDeliveryGrantStore
{
    public async ValueTask<Result> AddAsync(
        DeliveryGrantRecord grant,
        CancellationToken cancellationToken) =>
        await store.AddDeliveryGrantAsync(
            ToPersistence(grant),
            cancellationToken)
            ? Result.Success()
            : Result.Failure(DeliveryGrantErrors.InvalidRequest);

    public async ValueTask<DeliveryGrantRecord?> FindAsync(
        Guid grantId,
        CancellationToken cancellationToken)
    {
        PersistedDeliveryGrant? persisted =
            await store.FindDeliveryGrantAsync(grantId, cancellationToken);
        return persisted is null ? null : ToAuth(persisted);
    }

    public async ValueTask<DeliveryGrantRecord?> RevokeAsync(
        Guid tenantId,
        Guid grantId,
        long expectedVersion,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        PersistedDeliveryGrant? persisted =
            await store.RevokeDeliveryGrantAsync(
                tenantId,
                grantId,
                expectedVersion,
                revokedAtUtc,
                cancellationToken);
        return persisted is null ? null : ToAuth(persisted);
    }

    private static PersistedDeliveryGrant ToPersistence(
        DeliveryGrantRecord grant) =>
        new(
            grant.GrantId,
            grant.Version,
            grant.TenantId,
            grant.Identity.SubjectId,
            grant.Identity.ShareId,
            grant.Identity.ShareVersion,
            grant.Resource.AssetId,
            grant.Resource.RevisionId,
            grant.Resource.Rendition.Kind.ToString(),
            grant.Resource.Rendition.Identifier,
            grant.Permission.ToString(),
            grant.IssuedAtUtc,
            grant.NotBeforeUtc,
            grant.ExpiresAtUtc,
            grant.PepperVersionId,
            grant.TokenDigestHex,
            grant.RevokedAtUtc);

    private static DeliveryGrantRecord ToAuth(PersistedDeliveryGrant grant) =>
        new(
            grant.Id,
            grant.Version,
            grant.TenantId,
            new DeliveryGrantIdentity(
                grant.SubjectId,
                grant.ShareId,
                grant.ShareVersion),
            new DeliveryGrantResource(
                grant.AssetId,
                grant.RevisionId,
                ToRendition(
                    grant.RenditionKind,
                    grant.RenditionIdentifier)),
            Enum.Parse<DeliveryGrantAccess>(grant.Permission),
            grant.IssuedAtUtc,
            grant.NotBeforeUtc,
            grant.ExpiresAtUtc,
            grant.PepperVersionId,
            grant.TokenDigestHex,
            grant.RevokedAtUtc);

    private static DeliveryGrantRendition ToRendition(
        string kind,
        string identifier) =>
        Enum.Parse<DeliveryGrantRenditionKind>(kind) switch
        {
            DeliveryGrantRenditionKind.Derivative =>
                DeliveryGrantRendition.Derivative(identifier),
            DeliveryGrantRenditionKind.Original =>
                DeliveryGrantRendition.Original(),
            DeliveryGrantRenditionKind.Metadata =>
                DeliveryGrantRendition.Metadata(),
            _ => throw new InvalidOperationException(
                "The persisted delivery rendition is invalid."),
        };
}

internal sealed class PlatformCookieAuthAuditSink(
    RelationalAuthenticationStore store,
    ILogger<PlatformCookieAuthAuditSink> logger) : ICookieAuthAuditSink
{
    public async ValueTask WriteAsync(
        CookieAuthAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (auditEvent.TenantId is null)
        {
            PlatformAuthenticationAuditLog.UnknownTenant(
                logger,
                $"cookie_auth.{auditEvent.Action}",
                auditEvent.ReasonCode ?? "none",
                null);
            return;
        }

        await store.WriteAuditAsync(
            new PersistedAuthenticationAuditEvent(
                auditEvent.TenantId.Value.Value,
                auditEvent.UserId?.Value,
                auditEvent.UserId.HasValue ? "User" : "System",
                $"cookie_auth.{auditEvent.Action}",
                "CookieSession",
                auditEvent.UserId?.Value.ToString("D") ?? "[unknown]",
                IsRejected(auditEvent.Action) ? "Rejected" : "Succeeded",
                auditEvent.OccurredAt),
            cancellationToken);
    }

    private static bool IsRejected(CookieAuthAuditAction action) =>
        action is
            CookieAuthAuditAction.LoginRejected or
            CookieAuthAuditAction.SessionRejected;
}

internal sealed class PlatformApiKeyAuditSink(
    RelationalAuthenticationStore store,
    ILogger<PlatformApiKeyAuditSink> logger) : IApiKeyAuditSink
{
    public async ValueTask WriteAsync(
        ApiKeyAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (auditEvent.TenantId is null)
        {
            PlatformAuthenticationAuditLog.UnknownTenant(
                logger,
                $"api_key.{auditEvent.Action}",
                auditEvent.ReasonCode ?? "none",
                null);
            return;
        }

        await store.WriteAuditAsync(
            new PersistedAuthenticationAuditEvent(
                auditEvent.TenantId.Value.Value,
                auditEvent.ActorUserId?.Value,
                auditEvent.ActorUserId.HasValue ? "User" : "ApiKey",
                $"api_key.{auditEvent.Action}",
                "ApiKey",
                auditEvent.KeyId?.Value.ToString("D") ?? "[unknown]",
                auditEvent.Action == ApiKeyAuditAction.AuthenticationRejected
                    ? "Rejected"
                    : "Succeeded",
                auditEvent.OccurredAt),
            cancellationToken);
    }
}

internal sealed class PlatformDeliveryGrantAuditSink(
    RelationalAuthenticationStore store,
    ILogger<PlatformDeliveryGrantAuditSink> logger) : IDeliveryGrantAuditSink
{
    public async ValueTask WriteAsync(
        DeliveryGrantAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        if (!auditEvent.TenantId.HasValue)
        {
            PlatformAuthenticationAuditLog.UnknownTenant(
                logger,
                $"delivery_grant.{auditEvent.Action}",
                auditEvent.ReasonCode ?? "none",
                null);
            return;
        }

        await store.WriteAuditAsync(
            new PersistedAuthenticationAuditEvent(
                auditEvent.TenantId.Value,
                auditEvent.ActorId,
                auditEvent.ActorId.HasValue ? "User" : "System",
                $"delivery_grant.{auditEvent.Action}",
                "DeliveryGrant",
                auditEvent.GrantId?.ToString("D") ?? "[unknown]",
                auditEvent.Action == DeliveryGrantAuditAction.ValidationRejected
                    ? "Rejected"
                    : "Succeeded",
                auditEvent.OccurredAtUtc),
            cancellationToken);
    }
}

internal static class PlatformAuthenticationAuditLog
{
    internal static readonly Action<ILogger, string, string, Exception?>
        UnknownTenant = LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "AuthenticationAudit"),
            "Authentication event {Action} completed with reason {ReasonCode}.");
}
