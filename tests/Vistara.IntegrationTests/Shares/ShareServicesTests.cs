using Vistara.Application.Common;
using Vistara.Application.Sharing;
using Vistara.Auth.Delivery;
using Vistara.Auth.Sharing;
using Xunit;

namespace Vistara.IntegrationTests.Shares;

public sealed class ShareServicesTests
{
    private static readonly DateTimeOffset Now =
        new(2033, 4, 5, 6, 7, 8, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7(Now);
    private static readonly Guid OtherTenantId = Guid.CreateVersion7(Now.AddMilliseconds(1));
    private static readonly Guid ActorId = Guid.CreateVersion7(Now.AddMilliseconds(2));
    private static readonly Guid ShareId =
        Guid.Parse("0c9f2db3-417f-7ce4-a58a-8254ac528cbb");
    private static readonly Guid AssetId = Guid.CreateVersion7(Now.AddMilliseconds(3));
    private static readonly Guid RevisionId = Guid.CreateVersion7(Now.AddMilliseconds(4));

    [Fact]
    public async Task Shares_create_returns_256_bit_token_once_and_persists_only_peppered_hash()
    {
        var store = new FakeShareStore();
        var audit = new FakeShareAuditSink();
        var random = new RecordingRandomSource(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
        ShareService service = CreateService(store, audit, random: random);

        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(),
            "create-1",
            CancellationToken.None);
        ShareCreateResult replayed = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(),
            "create-1",
            CancellationToken.None);

        Assert.Equal(ShareCreateStatus.Created, created.Status);
        Assert.NotNull(created.PublicToken);
        Assert.Equal(32, random.LastDestinationLength);
        Assert.NotNull(store.Added);
        Assert.Equal(64, store.Added.TokenDigestHex.Length);
        Assert.DoesNotContain(created.PublicToken, store.CapturedText(), StringComparison.Ordinal);
        Assert.Equal(ShareCreateStatus.TokenAlreadyIssued, replayed.Status);
        Assert.Null(replayed.PublicToken);
        Assert.All(audit.Events, item => Assert.Equal(ShareAuditEvent.RedactedSecret, item.PresentedSecret));
        Assert.DoesNotContain(created.PublicToken, string.Join('|', audit.Events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shares_idempotency_detects_password_changes_without_storing_passwords()
    {
        var store = new FakeShareStore();
        ShareService service = CreateService(store);

        ShareCreateResult first = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(password: "first secure password"),
            "password-idempotency",
            CancellationToken.None);
        ShareCreateResult changed = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(password: "second secure password"),
            "password-idempotency",
            CancellationToken.None);

        Assert.Equal(ShareCreateStatus.Created, first.Status);
        Assert.Equal(ShareCreateStatus.IdempotencyConflict, changed.Status);
        Assert.DoesNotContain("first secure password", store.CapturedText(), StringComparison.Ordinal);
        Assert.DoesNotContain("second secure password", store.CapturedText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shares_wrong_password_is_rejected_then_rate_limited_without_issuing_session()
    {
        var store = new FakeShareStore();
        var audit = new FakeShareAuditSink();
        ShareService service = CreateService(
            store,
            audit,
            challengeLimit: 2);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(password: "correct horse battery staple"),
            "create-password",
            CancellationToken.None);

        ShareChallengeResult first = await service.ChallengeAsync(
            created.PublicToken!,
            "wrong",
            "198.51.100.12",
            CancellationToken.None);
        ShareChallengeResult second = await service.ChallengeAsync(
            created.PublicToken!,
            "still-wrong",
            "198.51.100.12",
            CancellationToken.None);
        ShareChallengeResult throttled = await service.ChallengeAsync(
            created.PublicToken!,
            "correct horse battery staple",
            "198.51.100.12",
            CancellationToken.None);

        Assert.Equal(ShareChallengeStatus.InvalidPassword, first.Status);
        Assert.Equal(ShareChallengeStatus.InvalidPassword, second.Status);
        Assert.Equal(ShareChallengeStatus.RateLimited, throttled.Status);
        Assert.NotNull(throttled.RetryAfter);
        Assert.Empty(store.Sessions);
        Assert.Equal(
            3,
            audit.Events.Count(item =>
                item.Action == ShareAuditAction.ChallengeRejected));
    }

    [Fact]
    public async Task Shares_expiry_revocation_and_share_version_invalidate_public_sessions()
    {
        var clock = new MutableClock(Now);
        var store = new FakeShareStore();
        ShareService service = CreateService(store, clock: clock);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(
                password: "correct horse battery staple",
                expiresAtUtc: Now.AddMinutes(30)),
            "create-lifecycle",
            CancellationToken.None);
        ShareChallengeResult challenge = await service.ChallengeAsync(
            created.PublicToken!,
            "correct horse battery staple",
            "203.0.113.8",
            CancellationToken.None);

        SharePublicResult active = await service.GetPublicAsync(
            null,
            challenge.SessionToken,
            60,
            null,
            CancellationToken.None);
        ShareMutationResult revoked = await service.RevokeAsync(
            Actor(TenantId),
            ShareId,
            created.Share!.Version,
            "revoke-lifecycle",
            CancellationToken.None);
        SharePublicResult afterRevoke = await service.GetPublicAsync(
            null,
            challenge.SessionToken,
            60,
            null,
            CancellationToken.None);

        Assert.Equal(SharePublicStatus.Available, active.Status);
        Assert.Equal(ShareMutationStatus.Updated, revoked.Status);
        Assert.Equal(SharePublicStatus.Gone, afterRevoke.Status);

        var expiringStore = new FakeShareStore();
        ShareService expiring = CreateService(expiringStore, clock: clock);
        ShareCreateResult expiringShare = await expiring.CreateAsync(
            Actor(TenantId),
            CreateCommand(expiresAtUtc: Now.AddMinutes(1)),
            "create-expiry",
            CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(1);
        SharePublicResult expired = await expiring.GetPublicAsync(
            expiringShare.PublicToken,
            null,
            60,
            null,
            CancellationToken.None);
        Assert.Equal(SharePublicStatus.Gone, expired.Status);
    }

    [Fact]
    public async Task Shares_snapshot_stays_bound_to_captured_revision()
    {
        var catalog = new MutableShareAssetCatalog(
            new ShareAssetSnapshot(
                AssetId,
                RevisionId,
                4,
                9,
                "Original title",
                "Private description",
                Now.AddDays(-1),
                1200,
                800,
                [
                    new ShareRendition(
                        "thumb",
                        "/media/revision-4.webp",
                        300,
                        200,
                        "image/webp",
                        ShareAccess.View,
                        "thumbnail.webp"),
                ]));
        var store = new FakeShareStore();
        ShareService service = CreateService(store, catalog: catalog);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(snapshotVersion: 9),
            "create-snapshot",
            CancellationToken.None);
        catalog.Current = catalog.Current with
        {
            RevisionId = Guid.CreateVersion7(Now.AddDays(1)),
            RevisionNumber = 5,
            AssetVersion = 10,
            Title = "Changed title",
            Renditions =
            [
                new ShareRendition(
                    "thumb",
                    "/media/revision-5.webp",
                    300,
                    200,
                    "image/webp",
                    ShareAccess.View,
                    "thumbnail.webp"),
            ],
        };
        ShareCreateResult replayed = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(snapshotVersion: 9),
            "create-snapshot",
            CancellationToken.None);

        SharePublicResult result = await service.GetPublicAsync(
            created.PublicToken,
            null,
            60,
            null,
            CancellationToken.None);

        Assert.Equal(ShareCreateStatus.TokenAlreadyIssued, replayed.Status);
        Assert.Equal(SharePublicStatus.Available, result.Status);
        ShareAssetSnapshot asset = Assert.Single(result.Share!.Assets);
        Assert.Equal(4, asset.RevisionNumber);
        Assert.Equal(9, asset.AssetVersion);
        Assert.Equal("Original title", asset.Title);
        Assert.Equal("/media/revision-4.webp", Assert.Single(asset.Renditions).Path);
    }

    [Fact]
    public void Shares_cursors_are_signed_and_tamper_evident()
    {
        var peppers = new SharePepperSet(
            "v1",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)31, 32).ToArray(),
            });
        var protector = new ShareCursorProtector(peppers);
        var state = new ShareCursorState(
            ShareCursorKind.Managed,
            TenantId,
            null,
            null,
            "active",
            Now,
            ShareId,
            0,
            Now.AddMinutes(15));

        string cursor = protector.Protect(state);
        bool valid = protector.TryUnprotect(cursor, out ShareCursorState parsed);
        int payloadStart = cursor.IndexOf('_', "vsc_".Length) + 1;
        int signatureStart = cursor.LastIndexOf('.') + 1;
        bool payloadTampered = protector.TryUnprotect(
            ReplaceCharacter(cursor, payloadStart),
            out _);
        bool signatureTampered = protector.TryUnprotect(
            ReplaceCharacter(cursor, signatureStart),
            out _);
        bool signatureEncodingTampered = protector.TryUnprotect(
            ReplaceTrailingBase64UrlPaddingBits(cursor),
            out _);

        Assert.True(valid);
        Assert.Equal(state, parsed);
        Assert.False(payloadTampered);
        Assert.False(signatureTampered);
        Assert.False(signatureEncodingTampered);
    }

    [Fact]
    public async Task Shares_permissions_never_escalate_downloads_or_private_metadata()
    {
        var catalog = new MutableShareAssetCatalog(
            new ShareAssetSnapshot(
                AssetId,
                RevisionId,
                4,
                9,
                "Safe title",
                "Private description",
                Now.AddDays(-1),
                1200,
                800,
                [
                    new ShareRendition(
                        "thumb",
                        "/media/thumb.webp",
                        300,
                        200,
                        "image/webp",
                        ShareAccess.View,
                        "thumbnail.webp"),
                    new ShareRendition(
                        "download",
                        "/delivery/rendition",
                        1200,
                        800,
                        "image/webp",
                        ShareAccess.DownloadRenditions,
                        "download.webp"),
                    new ShareRendition(
                        "download",
                        "/delivery/original",
                        1200,
                        800,
                        "image/jpeg",
                        ShareAccess.DownloadOriginal,
                        "original"),
                ]));
        ShareService service = CreateService(new FakeShareStore(), catalog: catalog);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(
                permissions: ShareAccess.View,
                metadataExposure: ShareMetadataExposure.None),
            "create-permissions",
            CancellationToken.None);

        SharePublicResult result = await service.GetPublicAsync(
            created.PublicToken,
            null,
            60,
            null,
            CancellationToken.None);

        ShareAssetSnapshot asset = Assert.Single(result.Share!.Assets);
        Assert.Null(asset.Description);
        Assert.Null(asset.CapturedAtUtc);
        Assert.DoesNotContain(
            asset.Renditions,
            item => item.RequiredAccess != ShareAccess.View);
        Assert.DoesNotContain(
            result.Share.GetType().GetProperties(),
            property => property.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Shares_delivery_authorization_is_revision_and_permission_bound()
    {
        var store = new FakeShareStore();
        ShareService service = CreateService(store);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(
                permissions: ShareAccess.View,
                metadataExposure: ShareMetadataExposure.None),
            "create-delivery",
            CancellationToken.None);
        var authorization = new ShareDeliveryGrantAuthorizationPort(
            new MutableClock(Now),
            store);
        var identity = new DeliveryGrantIdentity(
            null,
            ShareId,
            created.Share!.Version);

        DeliveryGrantAuthorizationDecision view =
            await authorization.AuthorizeIssueAsync(
                DeliveryRequest(
                    identity,
                    RevisionId,
                    DeliveryGrantRendition.Derivative("thumbnail.webp"),
                    DeliveryGrantAccess.ReadDerivative),
                CancellationToken.None);
        DeliveryGrantAuthorizationDecision original =
            await authorization.AuthorizeIssueAsync(
                DeliveryRequest(
                    identity,
                    RevisionId,
                    DeliveryGrantRendition.Original(),
                    DeliveryGrantAccess.ReadOriginal),
                CancellationToken.None);
        DeliveryGrantAuthorizationDecision metadata =
            await authorization.AuthorizeIssueAsync(
                DeliveryRequest(
                    identity,
                    RevisionId,
                    DeliveryGrantRendition.Metadata(),
                    DeliveryGrantAccess.ReadMetadata),
                CancellationToken.None);
        DeliveryGrantAuthorizationDecision wrongRevision =
            await authorization.AuthorizeIssueAsync(
                DeliveryRequest(
                    identity,
                    Guid.CreateVersion7(Now.AddDays(2)),
                    DeliveryGrantRendition.Derivative("thumbnail.webp"),
                    DeliveryGrantAccess.ReadDerivative),
                CancellationToken.None);

        Assert.True(view.IsAuthorized);
        Assert.False(original.IsAuthorized);
        Assert.False(metadata.IsAuthorized);
        Assert.True(wrongRevision.IsConcealed);
    }

    [Fact]
    public async Task Shares_cannot_escalate_metadata_after_private_fields_were_discarded()
    {
        var store = new FakeShareStore();
        ShareService service = CreateService(store);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(metadataExposure: ShareMetadataExposure.None),
            "metadata-create",
            CancellationToken.None);

        ShareMutationResult result = await service.UpdateAsync(
            Actor(TenantId),
            ShareId,
            created.Share!.Version,
            new ShareUpdateCommand(
                null,
                null,
                ShareMetadataExposure.Basic,
                null,
                false),
            "metadata-escalation",
            CancellationToken.None);

        Assert.Equal(ShareMutationStatus.Invalid, result.Status);
        Assert.Equal(ShareMetadataExposure.None, store.Added!.MetadataExposure);
        Assert.Null(Assert.Single(store.Added.Assets).Description);
    }

    [Fact]
    public async Task Shares_private_management_conceals_cross_tenant_and_redacts_public_credentials()
    {
        var audit = new FakeShareAuditSink();
        var store = new FakeShareStore();
        ShareService service = CreateService(store, audit);
        ShareCreateResult created = await service.CreateAsync(
            Actor(TenantId),
            CreateCommand(),
            "create-concealment",
            CancellationToken.None);

        ShareReadResult concealed = await service.GetAsync(
            Actor(OtherTenantId),
            ShareId,
            CancellationToken.None);
        SharePublicResult malformed = await service.GetPublicAsync(
            "secret-in-a-url",
            null,
            60,
            null,
            CancellationToken.None);

        Assert.Equal(ShareReadStatus.NotFound, concealed.Status);
        Assert.Equal(SharePublicStatus.NotFound, malformed.Status);
        Assert.DoesNotContain(created.PublicToken!, string.Join('|', audit.Events), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-in-a-url", string.Join('|', audit.Events), StringComparison.Ordinal);
        Assert.Equal(
            ShareAuditEvent.RedactedSecret,
            new SharePublicCredential("secret-in-a-url", null).ToString());
    }

    private static ShareService CreateService(
        FakeShareStore store,
        FakeShareAuditSink? audit = null,
        RecordingRandomSource? random = null,
        MutableClock? clock = null,
        MutableShareAssetCatalog? catalog = null,
        int challengeLimit = 5)
    {
        var peppers = new SharePepperSet(
            "v1",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)91, 32).ToArray(),
            });
        return new ShareService(
            clock ?? new MutableClock(Now),
            new FixedUuid7Generator(ShareId),
            new ShareTokenProtector(
                random ?? new RecordingRandomSource(Enumerable.Repeat((byte)17, 64).ToArray()),
                peppers),
            new Pbkdf2SharePasswordHasher(
                random ?? new RecordingRandomSource(Enumerable.Repeat((byte)17, 64).ToArray()),
                peppers,
                iterations: 10_000),
            new ShareSessionProtector(
                random ?? new RecordingRandomSource(Enumerable.Repeat((byte)29, 64).ToArray()),
                peppers),
            new ShareCursorProtector(peppers),
            store,
            catalog ?? new MutableShareAssetCatalog(
                new ShareAssetSnapshot(
                    AssetId,
                    RevisionId,
                    4,
                    9,
                    "Title",
                    "Description",
                    Now,
                    1200,
                    800,
                    [
                        new ShareRendition(
                            "thumb",
                            "/media/thumb.webp",
                            300,
                            200,
                            "image/webp",
                            ShareAccess.View,
                            "thumbnail.webp"),
                    ])),
            new InMemoryShareChallengeRateLimiter(),
            audit ?? new FakeShareAuditSink(),
            new ShareOptions(
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(5),
                challengeLimit));
    }

    private static ShareActor Actor(Guid tenantId) => new(tenantId, ActorId);

    private static string ReplaceCharacter(string value, int index)
    {
        char replacement = value[index] == 'A' ? 'B' : 'A';
        return value[..index] + replacement + value[(index + 1)..];
    }

    private static string ReplaceTrailingBase64UrlPaddingBits(string value)
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        int index = alphabet.IndexOf(value[^1], StringComparison.Ordinal);
        if (index < 0 || index % 4 != 0)
        {
            throw new InvalidOperationException(
                "A 256-bit signature must end in a canonical Base64URL character.");
        }

        return value[..^1] + alphabet[index + 1];
    }

    private static DeliveryGrantIssueRequest DeliveryRequest(
        DeliveryGrantIdentity identity,
        Guid revisionId,
        DeliveryGrantRendition rendition,
        DeliveryGrantAccess access) =>
        new(
            TenantId,
            identity,
            new DeliveryGrantResource(AssetId, revisionId, rendition),
            access,
            Now,
            Now.AddMinutes(5));

    private static ShareCreateCommand CreateCommand(
        string? password = null,
        DateTimeOffset? expiresAtUtc = null,
        long snapshotVersion = 9,
        ShareAccess permissions = ShareAccess.View,
        ShareMetadataExposure metadataExposure = ShareMetadataExposure.Basic) =>
        new(
            "Private gallery",
            ShareTargetType.Snapshot,
            null,
            [new ShareAssetReference(AssetId, snapshotVersion)],
            permissions,
            metadataExposure,
            expiresAtUtc,
            password);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class FixedUuid7Generator(Guid value) : IUuid7Generator
    {
        public Guid NewId() => value;
    }

    private sealed class RecordingRandomSource(byte[] bytes) : IShareRandomSource
    {
        public int LastDestinationLength { get; private set; }

        public void Fill(Span<byte> destination)
        {
            LastDestinationLength = destination.Length;
            bytes.AsSpan(0, destination.Length).CopyTo(destination);
        }
    }

    private sealed class MutableShareAssetCatalog(ShareAssetSnapshot current) : IShareAssetCatalog
    {
        public ShareAssetSnapshot Current { get; set; } = current;

        public ValueTask<IReadOnlyList<ShareAssetSnapshot>?> CaptureSnapshotAsync(
            Guid tenantId,
            ShareTargetType targetType,
            Guid? albumId,
            IReadOnlyList<ShareAssetReference> assets,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tenantId != TenantId ||
                assets.Count != 1 ||
                assets[0].AssetId != Current.AssetId ||
                assets[0].Version != Current.AssetVersion)
            {
                return ValueTask.FromResult<IReadOnlyList<ShareAssetSnapshot>?>(null);
            }

            return ValueTask.FromResult<IReadOnlyList<ShareAssetSnapshot>?>(
                [Current with { Renditions = Current.Renditions.ToArray() }]);
        }
    }

    private sealed class FakeShareStore : IShareStore
    {
        private readonly Dictionary<string, ShareRecord> _idempotency = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _mutations =
            new(StringComparer.Ordinal);
        private ShareRecord? _record;

        public ShareRecord? Added { get; private set; }

        public List<ShareSessionRecord> Sessions { get; } = [];

        public string CapturedText() => string.Join('|', Added, Sessions);

        public ValueTask<ShareIdempotencyRecord?> FindIdempotencyAsync(
            string idempotencyKeyHash,
            CancellationToken cancellationToken)
        {
            if (_idempotency.TryGetValue(
                    idempotencyKeyHash,
                    out ShareRecord? created))
            {
                return ValueTask.FromResult<ShareIdempotencyRecord?>(
                    new ShareIdempotencyRecord(
                        created.Id,
                        created.RequestHash));
            }

            return ValueTask.FromResult(
                _mutations.TryGetValue(
                    idempotencyKeyHash,
                    out string? requestHash)
                    ? new ShareIdempotencyRecord(_record!.Id, requestHash)
                    : null);
        }

        public ValueTask<ShareAddResult> AddAsync(
            ShareRecord share,
            string idempotencyKeyHash,
            string requestHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_idempotency.TryGetValue(idempotencyKeyHash, out ShareRecord? existing))
            {
                return ValueTask.FromResult(
                    existing.RequestHash == requestHash
                        ? ShareAddResult.Replayed(existing)
                        : ShareAddResult.IdempotencyConflict());
            }

            Added = share;
            _record = share;
            _idempotency.Add(idempotencyKeyHash, share);
            return ValueTask.FromResult(ShareAddResult.Created(share));
        }

        public ValueTask<ShareRecord?> FindAsync(
            Guid tenantId,
            Guid shareId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _record is { } record &&
                record.TenantId == tenantId &&
                record.Id == shareId
                    ? record
                    : null);

        public ValueTask<ShareRecord?> FindByIdAsync(
            Guid shareId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _record is { } record && record.Id == shareId
                    ? record
                    : null);

        public ValueTask<ShareRecord?> FindByTokenDigestAsync(
            string pepperVersionId,
            string digestHex,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _record is { } record &&
                record.PepperVersionId == pepperVersionId &&
                record.TokenDigestHex == digestHex
                    ? record
                    : null);

        public ValueTask<IReadOnlyList<ShareRecord>> ListAsync(
            Guid tenantId,
            int limit,
            string? status,
            DateTimeOffset nowUtc,
            DateTimeOffset? beforeCreatedAtUtc,
            Guid? beforeId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ShareRecord>>(
                _record is { } record && record.TenantId == tenantId
                    ? [record]
                    : []);

        public ValueTask<ShareUpdateResult> UpdateAsync(
            ShareRecord updated,
            long expectedVersion,
            string idempotencyKeyHash,
            string requestHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_mutations.TryGetValue(
                    idempotencyKeyHash,
                    out string? existingHash))
            {
                return ValueTask.FromResult(
                    existingHash == requestHash
                        ? ShareUpdateResult.Replayed(_record!)
                        : ShareUpdateResult.IdempotencyConflict());
            }

            if (_record is null || _record.TenantId != updated.TenantId || _record.Id != updated.Id)
            {
                return ValueTask.FromResult(ShareUpdateResult.NotFound());
            }

            if (_record.Version != expectedVersion)
            {
                return ValueTask.FromResult(ShareUpdateResult.VersionConflict());
            }

            _record = updated;
            Added = updated;
            _mutations.Add(idempotencyKeyHash, requestHash);
            return ValueTask.FromResult(
                updated.Version == expectedVersion
                    ? ShareUpdateResult.Unchanged(updated)
                    : ShareUpdateResult.Updated(updated));
        }

        public ValueTask AddSessionAsync(
            ShareSessionRecord session,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sessions.Add(session);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ShareSessionRecord?> FindSessionAsync(
            string pepperVersionId,
            string digestHex,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Sessions.SingleOrDefault(item =>
                    item.PepperVersionId == pepperVersionId &&
                    item.DigestHex == digestHex &&
                    item.ExpiresAtUtc > nowUtc));
    }

    private sealed class FakeShareAuditSink : IShareAuditSink
    {
        public List<ShareAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            ShareAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }
}
