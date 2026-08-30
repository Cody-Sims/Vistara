using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.ApiKeys;

public sealed record ApiKeyIssueRequest(
    TenantId TenantId,
    UserId OwnerId,
    ApiKeyScope Scopes,
    DateTimeOffset? ExpiresAt);

public sealed record IssuedApiKey(
    ApiKeyId KeyId,
    TenantId TenantId,
    UserId OwnerId,
    string Prefix,
    ApiKeyScope Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string PlaintextKey);

public sealed record ApiKeyAuthenticationRecord(
    ApiKeyMetadata Metadata,
    TenantStatus TenantStatus);

public sealed record ApiKeyPrincipal(
    ApiKeyId KeyId,
    TenantId TenantId,
    UserId OwnerId,
    ApiKeyScope Scopes);

public interface IApiKeyStore
{
    ValueTask<Result> AddAsync(
        ApiKeyMetadata metadata,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyAuthenticationRecord?> FindForAuthenticationAsync(
        ApiKeyId keyId,
        CancellationToken cancellationToken);

    ValueTask<Result> RevokeAsync(
        TenantId tenantId,
        ApiKeyId keyId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    ValueTask RecordLastUsedAsync(
        TenantId tenantId,
        ApiKeyId keyId,
        DateTimeOffset coarseUsedAt,
        CancellationToken cancellationToken);
}

public interface IApiKeyRandomSource
{
    void Fill(Span<byte> destination);
}

public interface IApiKeyPepperProvider
{
    string CurrentVersionId { get; }

    bool TryGetPepper(string versionId, out ReadOnlyMemory<byte> pepper);
}

public interface IApiKeyDigestComparer
{
    bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual);
}

public interface IApiKeyAuditSink
{
    ValueTask WriteAsync(
        ApiKeyAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public enum ApiKeyAuditAction
{
    Issued,
    Authenticated,
    AuthenticationRejected,
    Revoked,
}

public sealed record ApiKeyAuditEvent
{
    public const string RedactedPresentedKey = "[REDACTED]";

    public ApiKeyAuditEvent(
        ApiKeyAuditAction action,
        TenantId? tenantId,
        ApiKeyId? keyId,
        UserId? actorUserId,
        string? reasonCode,
        DateTimeOffset occurredAt)
    {
        if (occurredAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Audit timestamps must use UTC.", nameof(occurredAt));
        }

        Action = action;
        TenantId = tenantId;
        KeyId = keyId;
        ActorUserId = actorUserId;
        ReasonCode = reasonCode;
        OccurredAt = occurredAt;
    }

    public ApiKeyAuditAction Action { get; }

    public TenantId? TenantId { get; }

    public ApiKeyId? KeyId { get; }

    public UserId? ActorUserId { get; }

    public string? ReasonCode { get; }

    public DateTimeOffset OccurredAt { get; }

    public string PresentedKey { get; } = RedactedPresentedKey;
}
