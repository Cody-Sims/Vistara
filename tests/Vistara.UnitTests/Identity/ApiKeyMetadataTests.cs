using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.UnitTests.Identity;

public sealed class ApiKeyMetadataTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    private static readonly TenantId TenantId = new(Guid.CreateVersion7(CreatedAt));
    private static readonly UserId OwnerId = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(1)));

    [Fact]
    public void Create_normalizes_hash_metadata_and_binds_key_to_tenant_owner_and_scopes()
    {
        Result<ApiKeyMetadata> result = ApiKeyMetadata.Create(
            new ApiKeyId(Guid.CreateVersion7(CreatedAt.AddMilliseconds(2))),
            TenantId,
            OwnerId,
            " VST_01ABC ",
            new string('A', 64),
            ApiKeyScope.ReadAssets | ApiKeyScope.UploadAssets,
            CreatedAt,
            CreatedAt.AddDays(30));

        Assert.True(result.TryGetValue(out ApiKeyMetadata? key));
        Assert.Equal("vst_01abc", key.Prefix.Value);
        Assert.Equal(new string('a', 64), key.Digest.Value);
        Assert.Equal(TenantId, key.TenantId);
        Assert.Equal(OwnerId, key.OwnerId);
        Assert.True(key.HasScope(ApiKeyScope.ReadAssets));
        Assert.False(key.HasScope(ApiKeyScope.ManageApiKeys));
        Assert.True(key.IsForTenant(TenantId));
        Assert.Equal(ApiKeyStatus.Active, key.GetStatus(CreatedAt));
        Assert.Equal(1, key.Version);
    }

    [Fact]
    public void Create_rejects_raw_or_malformed_metadata_empty_scopes_and_invalid_expiry()
    {
        ApiKeyId id = new(Guid.CreateVersion7(CreatedAt.AddMilliseconds(2)));

        Result<ApiKeyMetadata> rawSecret = ApiKeyMetadata.Create(
            id,
            TenantId,
            OwnerId,
            "vst_01abc",
            "raw-secret",
            ApiKeyScope.ReadAssets,
            CreatedAt,
            null);
        Result<ApiKeyMetadata> noScopes = ApiKeyMetadata.Create(
            id,
            TenantId,
            OwnerId,
            "vst_01abc",
            new string('a', 64),
            ApiKeyScope.None,
            CreatedAt,
            null);
        Result<ApiKeyMetadata> expired = ApiKeyMetadata.Create(
            id,
            TenantId,
            OwnerId,
            "vst_01abc",
            new string('a', 64),
            ApiKeyScope.ReadAssets,
            CreatedAt,
            CreatedAt);

        Assert.Equal("identity.invalid_api_key_digest", rawSecret.Error?.Code);
        Assert.Equal("identity.api_key_scopes_required", noScopes.Error?.Code);
        Assert.Equal("identity.api_key_expiry_invalid", expired.Error?.Code);
    }

    [Fact]
    public void Usage_revocation_and_expiry_are_deterministic_and_versioned()
    {
        ApiKeyMetadata key = CreateKey(CreatedAt.AddHours(1));
        DateTimeOffset coarseUsedAt =
            new(2030, 4, 5, 6, 8, 0, TimeSpan.Zero);

        Assert.True(key.RecordUsed(CreatedAt.AddMinutes(1).AddSeconds(30)).IsSuccess);
        Assert.Equal(coarseUsedAt, key.LastUsedAt);
        Assert.Equal(2, key.Version);

        Assert.True(key.RecordUsed(CreatedAt.AddMinutes(1).AddSeconds(45)).IsSuccess);
        Assert.Equal(coarseUsedAt, key.LastUsedAt);
        Assert.Equal(2, key.Version);

        Assert.True(key.Revoke(CreatedAt.AddMinutes(2)).IsSuccess);
        Assert.Equal(ApiKeyStatus.Revoked, key.GetStatus(CreatedAt.AddMinutes(2)));
        Assert.Equal(3, key.Version);

        Result useRevoked = key.RecordUsed(CreatedAt.AddMinutes(3));
        Result duplicateRevoke = key.Revoke(CreatedAt.AddMinutes(3));
        Assert.Equal("identity.api_key_revoked", useRevoked.Error?.Code);
        Assert.Equal("identity.api_key_already_revoked", duplicateRevoke.Error?.Code);

        ApiKeyMetadata expiring = CreateKey(CreatedAt.AddHours(1));
        Assert.Equal(ApiKeyStatus.Active, expiring.GetStatus(expiring.ExpiresAt!.Value.AddTicks(-1)));
        Assert.Equal(ApiKeyStatus.Expired, expiring.GetStatus(expiring.ExpiresAt.Value));
        Assert.Equal(
            "identity.api_key_expired",
            expiring.RecordUsed(expiring.ExpiresAt.Value).Error?.Code);
    }

    private static ApiKeyMetadata CreateKey(DateTimeOffset? expiresAt)
    {
        Result<ApiKeyMetadata> result = ApiKeyMetadata.Create(
            new ApiKeyId(Guid.CreateVersion7(CreatedAt.AddMilliseconds(2))),
            TenantId,
            OwnerId,
            "vst_01abc",
            new string('a', 64),
            ApiKeyScope.ReadAssets | ApiKeyScope.UploadAssets,
            CreatedAt,
            expiresAt);
        Assert.True(result.TryGetValue(out ApiKeyMetadata? key));
        return key;
    }
}
