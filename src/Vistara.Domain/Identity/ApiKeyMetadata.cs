using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;

namespace Vistara.Domain.Identity;

public sealed class ApiKeyMetadata
{
    private const ApiKeyScope AllScopes =
        ApiKeyScope.ReadAssets |
        ApiKeyScope.UploadAssets |
        ApiKeyScope.ManageMetadata |
        ApiKeyScope.ManageApiKeys;

    private ApiKeyMetadata(
        ApiKeyId id,
        TenantId tenantId,
        UserId ownerId,
        ApiKeyPrefix prefix,
        ApiKeyDigest digest,
        ApiKeyScope scopes,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        Id = id;
        TenantId = tenantId;
        OwnerId = ownerId;
        Prefix = prefix;
        Digest = digest;
        Scopes = scopes;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public ApiKeyId Id { get; }

    public TenantId TenantId { get; }

    public UserId OwnerId { get; }

    public ApiKeyPrefix Prefix { get; }

    public ApiKeyDigest Digest { get; }

    public ApiKeyScope Scopes { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Result<ApiKeyMetadata> Create(
        ApiKeyId id,
        TenantId tenantId,
        UserId ownerId,
        string prefix,
        string digest,
        ApiKeyScope scopes,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        if (createdAt.Offset != TimeSpan.Zero ||
            (expiresAt.HasValue && expiresAt.Value.Offset != TimeSpan.Zero))
        {
            return Result.Failure<ApiKeyMetadata>(IdentityErrors.TimestampNotUtc);
        }

        Result<ApiKeyPrefix> prefixResult = ApiKeyPrefix.Create(prefix);
        if (!prefixResult.TryGetValue(out ApiKeyPrefix normalizedPrefix))
        {
            return Result.Failure<ApiKeyMetadata>(prefixResult.Error!);
        }

        Result<ApiKeyDigest> digestResult = ApiKeyDigest.Create(digest);
        if (!digestResult.TryGetValue(out ApiKeyDigest normalizedDigest))
        {
            return Result.Failure<ApiKeyMetadata>(digestResult.Error!);
        }

        if (scopes == ApiKeyScope.None || (scopes & ~AllScopes) != ApiKeyScope.None)
        {
            return Result.Failure<ApiKeyMetadata>(IdentityErrors.ApiKeyScopesRequired);
        }

        if (expiresAt <= createdAt)
        {
            return Result.Failure<ApiKeyMetadata>(IdentityErrors.ApiKeyExpiryInvalid);
        }

        return Result.Success(new ApiKeyMetadata(
            id,
            tenantId,
            ownerId,
            normalizedPrefix,
            normalizedDigest,
            scopes,
            createdAt,
            expiresAt));
    }

    public bool IsForTenant(TenantId tenantId) => TenantId == tenantId;

    public bool HasScope(ApiKeyScope scope) =>
        scope != ApiKeyScope.None &&
        (scope & ~AllScopes) == ApiKeyScope.None &&
        (Scopes & scope) == scope;

    public ApiKeyStatus GetStatus(DateTimeOffset at)
    {
        if (at.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                IdentityErrors.TimestampNotUtc.Message,
                nameof(at));
        }

        if (RevokedAt.HasValue)
        {
            return ApiKeyStatus.Revoked;
        }

        return ExpiresAt.HasValue && at >= ExpiresAt.Value
            ? ApiKeyStatus.Expired
            : ApiKeyStatus.Active;
    }

    public Result RecordUsed(DateTimeOffset usedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(usedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        ApiKeyStatus status = GetStatus(usedAt);
        if (status == ApiKeyStatus.Revoked)
        {
            return Result.Failure(IdentityErrors.ApiKeyRevoked);
        }

        if (status == ApiKeyStatus.Expired)
        {
            return Result.Failure(IdentityErrors.ApiKeyExpired);
        }

        DateTimeOffset coarseUsedAt = new(
            usedAt.Year,
            usedAt.Month,
            usedAt.Day,
            usedAt.Hour,
            usedAt.Minute,
            0,
            TimeSpan.Zero);
        if (LastUsedAt == coarseUsedAt)
        {
            return Result.Success();
        }

        LastUsedAt = coarseUsedAt;
        UpdatedAt = usedAt;
        Version++;
        return Result.Success();
    }

    public Result Revoke(DateTimeOffset revokedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(revokedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        if (RevokedAt.HasValue)
        {
            return Result.Failure(IdentityErrors.ApiKeyAlreadyRevoked);
        }

        RevokedAt = revokedAt;
        UpdatedAt = revokedAt;
        Version++;
        return Result.Success();
    }

    private Result ValidateMutationTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            return Result.Failure(IdentityErrors.TimestampNotUtc);
        }

        return timestamp < UpdatedAt
            ? Result.Failure(IdentityErrors.TimestampOutOfOrder)
            : Result.Success();
    }
}
