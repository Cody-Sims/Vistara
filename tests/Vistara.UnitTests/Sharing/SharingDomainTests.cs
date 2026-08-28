using Vistara.Domain.Common;
using Vistara.Domain.Sharing;

namespace Vistara.UnitTests.Sharing;

public sealed class SharingDomainTests
{
    private static readonly SharingTenantId Tenant = new(Guid.Parse("0195b111-1111-7111-8111-111111111111"));
    private static readonly SharingTenantId OtherTenant = new(Guid.Parse("0195b222-2222-7222-8222-222222222222"));
    private static readonly SharingUserId Owner = new(Guid.Parse("0195b333-3333-7333-8333-333333333333"));
    private static readonly DateTimeOffset CreatedAt = new(2030, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void Share_stores_hashed_token_metadata_and_expires_at_its_deadline()
    {
        ShareTokenHash tokenHash = Value(ShareTokenHash.FromHex(new string('a', 64)));
        ShareLink share = Value(ShareLink.Create(
            new ShareId(Guid.Parse("0195b444-4444-7444-8444-444444444444")),
            Tenant,
            Owner,
            tokenHash,
            ShareTarget.Album(new SharingAlbumId(Guid.Parse("0195b555-5555-7555-8555-555555555555"))),
            SharePermissions.View | SharePermissions.DownloadRenditions,
            CreatedAt,
            CreatedAt.AddHours(1),
            passwordHash: "argon2id$hashed-password"));

        Assert.Equal(tokenHash, share.TokenHash);
        Assert.Equal(ShareVisibility.Active, share.VisibilityAt(CreatedAt.AddMinutes(59)));
        Assert.Equal(ShareVisibility.Expired, share.VisibilityAt(CreatedAt.AddHours(1)));
        Assert.Equal("sharing.share_expired", share.Authorize(CreatedAt.AddHours(1)).Error?.Code);
    }

    [Fact]
    public void Revocation_is_immediate_and_idempotent()
    {
        ShareLink share = CreateShare(expiresAt: CreatedAt.AddDays(1));

        Result revoked = share.Revoke(Owner, CreatedAt.AddMinutes(5), share.Version);
        long versionAfterFirstRevoke = share.Version;
        Result repeated = share.Revoke(Owner, CreatedAt.AddMinutes(6), share.Version);

        Assert.True(revoked.IsSuccess);
        Assert.True(repeated.IsSuccess);
        Assert.Equal(versionAfterFirstRevoke, share.Version);
        Assert.Equal(ShareVisibility.Revoked, share.VisibilityAt(CreatedAt.AddMinutes(6)));
        Assert.Equal("sharing.share_revoked", share.Authorize(CreatedAt.AddMinutes(6)).Error?.Code);
    }

    [Fact]
    public void Snapshot_assets_and_resource_grants_reject_cross_tenant_references()
    {
        ShareLink share = CreateShare(expiresAt: null, target: ShareTarget.Snapshot());
        var localAsset = new SharedAssetRef(
            Tenant,
            new SharedAssetId(Guid.Parse("0195b666-6666-7666-8666-666666666666")),
            revision: 4);
        var foreignAsset = new SharedAssetRef(
            OtherTenant,
            new SharedAssetId(Guid.Parse("0195b777-7777-7777-8777-777777777777")),
            revision: 1);

        Assert.True(share.AddSnapshotAsset(localAsset, share.Version).IsSuccess);
        Assert.Equal(
            ErrorCategory.Forbidden,
            share.AddSnapshotAsset(foreignAsset, share.Version).Error?.Category);

        Result<ResourceGrant> grant = ResourceGrant.Create(
            new ResourceGrantId(Guid.Parse("0195b888-8888-7888-8888-888888888888")),
            Tenant,
            new GrantResourceRef(OtherTenant, ResourceKind.Album, Guid.Parse("0195b999-9999-7999-8999-999999999999")),
            new GranteeRef(GranteeKind.User, Owner.Value),
            GrantRole.Viewer,
            Owner,
            CreatedAt);

        Assert.Equal(ErrorCategory.Forbidden, grant.Error?.Category);
    }

    [Fact]
    public void Resource_grants_reject_undefined_kinds_and_roles_on_create_and_update()
    {
        ResourceGrantId grantId =
            new(Guid.Parse("0195b888-8888-7888-8888-888888888888"));
        Guid resourceId = Guid.Parse("0195b999-9999-7999-8999-999999999999");

        Result<ResourceGrant> invalidResourceKind = ResourceGrant.Create(
            grantId,
            Tenant,
            new GrantResourceRef(Tenant, (ResourceKind)999, resourceId),
            new GranteeRef(GranteeKind.User, Owner.Value),
            GrantRole.Viewer,
            Owner,
            CreatedAt);
        Result<ResourceGrant> invalidGranteeKind = ResourceGrant.Create(
            grantId,
            Tenant,
            new GrantResourceRef(Tenant, ResourceKind.Album, resourceId),
            new GranteeRef((GranteeKind)999, Owner.Value),
            GrantRole.Viewer,
            Owner,
            CreatedAt);
        Result<ResourceGrant> invalidRole = ResourceGrant.Create(
            grantId,
            Tenant,
            new GrantResourceRef(Tenant, ResourceKind.Album, resourceId),
            new GranteeRef(GranteeKind.User, Owner.Value),
            (GrantRole)999,
            Owner,
            CreatedAt);

        Assert.Equal("sharing.resource_kind_invalid", invalidResourceKind.Error?.Code);
        Assert.Equal("sharing.grantee_kind_invalid", invalidGranteeKind.Error?.Code);
        Assert.Equal("sharing.grant_role_invalid", invalidRole.Error?.Code);

        ResourceGrant grant = Value(ResourceGrant.Create(
            grantId,
            Tenant,
            new GrantResourceRef(Tenant, ResourceKind.Album, resourceId),
            new GranteeRef(GranteeKind.User, Owner.Value),
            GrantRole.Viewer,
            Owner,
            CreatedAt));
        Result changed = grant.ChangeRole((GrantRole)999, grant.Version);

        Assert.Equal("sharing.grant_role_invalid", changed.Error?.Code);
        Assert.Equal(GrantRole.Viewer, grant.Role);
    }

    [Fact]
    public void Share_permissions_reject_unknown_bits_on_create_and_update()
    {
        SharePermissions unknown =
            SharePermissions.View | (SharePermissions)(1 << 10);

        Result<ShareLink> created = ShareLink.Create(
            new ShareId(Guid.Parse("0195baaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa")),
            Tenant,
            Owner,
            Value(ShareTokenHash.FromHex(new string('b', 64))),
            ShareTarget.Snapshot(),
            unknown,
            CreatedAt);

        Assert.Equal("sharing.permissions_invalid", created.Error?.Code);

        ShareLink share = CreateShare(expiresAt: null, target: ShareTarget.Snapshot());
        Result changed = share.ChangeAccess(unknown, null, share.Version);

        Assert.Equal("sharing.permissions_invalid", changed.Error?.Code);
        Assert.Equal(SharePermissions.View, share.Permissions);
    }

    [Fact]
    public void Sharing_identifiers_require_non_empty_uuid7_values()
    {
        Action<Guid>[] constructors =
        [
            value => _ = new ShareId(value),
            value => _ = new ResourceGrantId(value),
            value => _ = new SharingTenantId(value),
            value => _ = new SharingUserId(value),
            value => _ = new SharingAlbumId(value),
            value => _ = new SharedAssetId(value),
        ];
        Guid versionFour = Guid.Parse("11111111-1111-4111-8111-111111111111");

        foreach (Action<Guid> construct in constructors)
        {
            Assert.Throws<ArgumentException>(() => construct(Guid.Empty));
            Assert.Throws<ArgumentException>(() => construct(versionFour));
        }
    }

    private static ShareLink CreateShare(DateTimeOffset? expiresAt, ShareTarget? target = null) =>
        Value(ShareLink.Create(
            new ShareId(Guid.Parse("0195baaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa")),
            Tenant,
            Owner,
            Value(ShareTokenHash.FromHex(new string('b', 64))),
            target ?? ShareTarget.Album(new SharingAlbumId(Guid.Parse("0195bbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb"))),
            SharePermissions.View,
            CreatedAt,
            expiresAt));

    private static T Value<T>(Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Message);
        return value;
    }
}
