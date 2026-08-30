using Vistara.Domain.Assets;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Domain.Uploads;
using Vistara.Persistence.Repositories;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

public sealed class PersistenceRepositoryTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Tenancy_and_identity_aggregates_round_trip_with_state()
    {
        Guid tenantGuid = Guid.CreateVersion7();
        var tenantId = new TenantId(tenantGuid);
        var userId = new UserId(Guid.CreateVersion7());
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantGuid);
        var users = new UserRepository(database.Context);
        var tenants = new TenantRepository(database.Context);
        var memberships = new TenantMembershipRepository(database.Context);

        User user = Required(User.Create(userId, "OWNER@Example.test", " Owner ", UtcNow));
        Assert.True(user.LinkLocalIdentity(
            new LocalIdentityId(Guid.CreateVersion7()),
            "Owner",
            UtcNow.AddMinutes(1)).IsSuccess);
        Assert.True(user.LinkExternalIdentity(
            new ExternalIdentityId(Guid.CreateVersion7()),
            "https://ID.example.test/",
            "subject-1",
            UtcNow.AddMinutes(2)).IsSuccess);
        await users.AddAsync(user, CancellationToken.None);

        Tenant tenant = Required(Tenant.Create(tenantId, "photos", "Photos", UtcNow));
        Assert.True(tenant.Rename("Family Photos", UtcNow.AddMinutes(1)).IsSuccess);
        Assert.True(tenant.Suspend(UtcNow.AddMinutes(2)).IsSuccess);
        await tenants.AddAsync(tenant, CancellationToken.None);

        TenantMembership membership = Required(TenantMembership.Invite(
            tenantId,
            userId,
            TenantRole.TenantOwner,
            UtcNow));
        Assert.True(membership.Activate(UtcNow.AddMinutes(1)).IsSuccess);
        await memberships.AddAsync(membership, CancellationToken.None);
        database.Context.ChangeTracker.Clear();

        User reloadedUser = Assert.IsType<User>(
            await users.FindByExternalIdentityAsync(
                Required(ExternalIssuer.Create("https://id.example.test")),
                "subject-1",
                CancellationToken.None));
        Tenant reloadedTenant = Assert.IsType<Tenant>(
            await tenants.FindBySlugAsync(
                Required(TenantSlug.Create("photos")),
                CancellationToken.None));
        TenantMembership reloadedMembership = Assert.IsType<TenantMembership>(
            await memberships.FindAsync(
                tenantId,
                userId,
                CancellationToken.None));

        Assert.Equal(3, reloadedUser.Version);
        Assert.Single(reloadedUser.LocalIdentities);
        Assert.Single(reloadedUser.ExternalIdentities);
        Assert.Equal(TenantStatus.Suspended, reloadedTenant.Status);
        Assert.Equal(3, reloadedTenant.Version);
        Assert.Equal(MembershipStatus.Active, reloadedMembership.Status);
        Assert.Equal(2, reloadedMembership.Version);
    }

    [Fact]
    public async Task Session_and_api_key_security_metadata_round_trip()
    {
        Guid tenantGuid = Guid.CreateVersion7();
        var tenantId = new TenantId(tenantGuid);
        var userId = new UserId(Guid.CreateVersion7());
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantGuid);
        var users = new UserRepository(database.Context);
        var tenants = new TenantRepository(database.Context);
        var sessions = new AuthSessionRepository(database.Context);
        var apiKeys = new ApiKeyRepository(database.Context);

        await users.AddAsync(
            Required(User.Create(userId, "owner@example.test", "Owner", UtcNow)),
            CancellationToken.None);
        await tenants.AddAsync(
            Required(Tenant.Create(tenantId, "tenant", "Tenant", UtcNow)),
            CancellationToken.None);

        AuthSession session = Required(AuthSession.Create(
            new AuthSessionId(Guid.CreateVersion7()),
            userId,
            new SessionDigest(new string('a', 64)),
            UtcNow,
            UtcNow.AddDays(30)));
        Assert.True(session.Revoke(UtcNow.AddMinutes(5)).IsSuccess);
        await sessions.AddAsync(session, CancellationToken.None);

        ApiKeyMetadata apiKey = Required(ApiKeyMetadata.Create(
            new ApiKeyId(Guid.CreateVersion7()),
            tenantId,
            userId,
            "vst_key123",
            new string('b', 64),
            ApiKeyScope.ReadAssets | ApiKeyScope.UploadAssets,
            UtcNow,
            UtcNow.AddDays(90)));
        Assert.True(apiKey.RecordUsed(UtcNow.AddMinutes(5).AddSeconds(20)).IsSuccess);
        Assert.True(apiKey.Revoke(UtcNow.AddMinutes(6)).IsSuccess);
        await apiKeys.AddAsync(apiKey, CancellationToken.None);
        database.Context.ChangeTracker.Clear();

        AuthSession reloadedSession = Assert.IsType<AuthSession>(
            await sessions.FindByDigestAsync(
                new SessionDigest(new string('a', 64)),
                CancellationToken.None));
        ApiKeyMetadata reloadedKey = Assert.IsType<ApiKeyMetadata>(
            await apiKeys.FindByPrefixAsync(
                Required(ApiKeyPrefix.Create("vst_key123")),
                CancellationToken.None));

        Assert.Equal(SessionStatus.Revoked, reloadedSession.GetStatus(UtcNow.AddHours(1)));
        Assert.Equal(2, reloadedSession.Version);
        Assert.Equal(ApiKeyStatus.Revoked, reloadedKey.GetStatus(UtcNow.AddHours(1)));
        Assert.Equal(UtcNow.AddMinutes(5), reloadedKey.LastUsedAt);
        Assert.Equal(3, reloadedKey.Version);
    }

    [Fact]
    public async Task Asset_revision_blob_and_metadata_round_trip()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid ownerId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid blobId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        var users = new UserRepository(database.Context);
        var tenants = new TenantRepository(database.Context);
        var blobs = new BlobMetadataRepository(database.Context);
        var assets = new AssetRepository(database.Context);

        await users.AddAsync(
            Required(User.Create(
                new UserId(ownerId),
                "owner@example.test",
                "Owner",
                UtcNow)),
            CancellationToken.None);
        await tenants.AddAsync(
            Required(Tenant.Create(
                new TenantId(tenantId),
                "tenant",
                "Tenant",
                UtcNow)),
            CancellationToken.None);

        var blob = new BlobObjectMetadata(
            blobId,
            tenantId,
            "local",
            "media",
            $"originals/{tenantId:N}/{assetId:N}/1/image.jpg",
            "v1",
            new Sha256Checksum(new string('c', 64)),
            "provider-checksum",
            42,
            new MediaContentType("image/jpeg"),
            UtcNow);
        await blobs.AddAsync(blob, CancellationToken.None);

        Asset asset = Asset.Create(
            assetId,
            tenantId,
            ownerId,
            "Original",
            AssetVisibility.Private,
            UtcNow);
        var revision = new AssetRevision(
            Guid.CreateVersion7(),
            tenantId,
            assetId,
            1,
            blob,
            new MediaDescriptor(
                "jpeg",
                new MediaContentType("image/jpeg"),
                new PixelDimensions(6, 7),
                1,
                new MediaPrivacyMetadata(
                    new Dictionary<string, string> { ["camera"] = "safe" },
                    new Dictionary<string, string> { ["gps"] = "private" })),
            UtcNow.AddMinutes(1));
        Assert.True(asset.AddRevision(revision, UtcNow.AddMinutes(1)).IsSuccess);
        Assert.True(asset.UpdateMetadata(
            "Renamed",
            "Description",
            AssetVisibility.Tenant,
            asset.Version,
            UtcNow.AddMinutes(2)).IsSuccess);
        await assets.AddAsync(asset, CancellationToken.None);
        database.Context.ChangeTracker.Clear();

        Asset reloaded = Assert.IsType<Asset>(
            await assets.GetAsync(
                tenantId,
                assetId,
                CancellationToken.None));

        Assert.Equal("Renamed", reloaded.Title);
        Assert.Equal(AssetVisibility.Tenant, reloaded.Visibility);
        Assert.Equal(3, reloaded.Version);
        AssetRevision reloadedRevision = Assert.Single(reloaded.Revisions);
        Assert.Equal(blobId, reloadedRevision.Original.Id);
        Assert.Equal(6, reloadedRevision.Media.Dimensions.Width);
        Assert.Equal("safe", reloadedRevision.Media.PrivacyMetadata.SafeProperties["camera"]);
        Assert.Equal("private", reloadedRevision.Media.PrivacyMetadata.PrivateProperties["gps"]);
    }

    [Fact]
    public async Task Multipart_upload_round_trips_parts_reservation_and_idempotency()
    {
        Guid tenantId = Guid.CreateVersion7();
        Guid actorId = Guid.CreateVersion7();
        Guid uploadId = Guid.CreateVersion7();
        await using PersistenceDatabase database =
            await PersistenceDatabase.CreateAsync(tenantId);
        var users = new UserRepository(database.Context);
        var tenants = new TenantRepository(database.Context);
        var uploads = new UploadSessionRepository(database.Context);

        await users.AddAsync(
            Required(User.Create(
                new UserId(actorId),
                "owner@example.test",
                "Owner",
                UtcNow)),
            CancellationToken.None);
        await tenants.AddAsync(
            Required(Tenant.Create(
                new TenantId(tenantId),
                "tenant",
                "Tenant",
                UtcNow)),
            CancellationToken.None);

        DateTimeOffset expiresAt = UtcNow.AddHours(1);
        var intent = new UploadIntent(
            tenantId,
            actorId,
            UploadStrategy.Multipart,
            new UploadIntegrityExpectation(
                42,
                new Sha256Checksum(new string('d', 64)),
                new MediaContentType("image/jpeg")),
            new UploadIdempotencyMetadata(
                "request-1",
                new Sha256Checksum(new string('e', 64)),
                expiresAt),
            UploadReservationMetadata.Create(
                Guid.CreateVersion7(),
                42,
                1,
                0,
                expiresAt));
        UploadSession upload = UploadSession.Create(
            uploadId,
            intent,
            $"staging/{tenantId:N}/{uploadId:N}",
            expiresAt,
            UtcNow);
        Assert.True(upload.Issue("provider-upload", UtcNow.AddMinutes(1)).IsSuccess);
        Assert.True(upload.RegisterPart(
            new UploadPart(1, "etag", "checksum", 42),
            UtcNow.AddMinutes(2)).IsSuccess);
        Assert.True(upload.RequestCommit(UtcNow.AddMinutes(3)).IsSuccess);
        await uploads.AddAsync(upload, CancellationToken.None);
        database.Context.ChangeTracker.Clear();

        UploadSession reloaded = Assert.IsType<UploadSession>(
            await uploads.FindByIdempotencyAsync(
                tenantId,
                actorId,
                "request-1",
                CancellationToken.None));

        Assert.Equal(UploadState.CommitRequested, reloaded.State);
        Assert.Equal(UploadReservationState.Reserved, reloaded.Reservation.State);
        Assert.Equal(new string('e', 64), reloaded.Idempotency.RequestHash.Value);
        Assert.Equal(4, reloaded.Version);
        UploadPart part = Assert.Single(reloaded.Parts);
        Assert.Equal("etag", part.EntityTag);
        Assert.Equal(42, part.SizeBytes);
    }

    private static T Required<T>(Vistara.Domain.Common.Result<T> result)
        where T : notnull
    {
        Assert.True(result.TryGetValue(out T? value), result.Error?.Message);
        return value;
    }
}
