using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Common;

namespace Vistara.Application.Sharing;

public sealed class ShareService
{
    private const ShareAccess AllAccess =
        ShareAccess.View |
        ShareAccess.DownloadRenditions |
        ShareAccess.DownloadOriginal;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly IShareTokenProtector _tokenProtector;
    private readonly ISharePasswordHasher _passwordHasher;
    private readonly IShareSessionProtector _sessionProtector;
    private readonly IShareCursorProtector _cursorProtector;
    private readonly IShareStore _store;
    private readonly IShareAssetCatalog _assetCatalog;
    private readonly IShareChallengeRateLimiter _rateLimiter;
    private readonly IShareAuditSink _audit;
    private readonly ShareOptions _options;

    public ShareService(
        IClock clock,
        IUuid7Generator idGenerator,
        IShareTokenProtector tokenProtector,
        ISharePasswordHasher passwordHasher,
        IShareSessionProtector sessionProtector,
        IShareCursorProtector cursorProtector,
        IShareStore store,
        IShareAssetCatalog assetCatalog,
        IShareChallengeRateLimiter rateLimiter,
        IShareAuditSink audit,
        ShareOptions options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _tokenProtector = tokenProtector ?? throw new ArgumentNullException(nameof(tokenProtector));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _sessionProtector = sessionProtector ?? throw new ArgumentNullException(nameof(sessionProtector));
        _cursorProtector = cursorProtector ?? throw new ArgumentNullException(nameof(cursorProtector));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _assetCatalog = assetCatalog ?? throw new ArgumentNullException(nameof(assetCatalog));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<ShareCreateResult> CreateAsync(
        ShareActor actor,
        ShareCreateCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        string? validationCode = ValidateCreate(command, idempotencyKey, now);
        if (validationCode is not null)
        {
            await AuditAsync(
                ShareAuditAction.CreateRejected,
                actor.TenantId,
                null,
                actor.ActorId,
                validationCode,
                now);
            return new(ShareCreateStatus.Invalid, null, null, validationCode);
        }

        string? passwordFingerprint = command.Password is null
            ? null
            : _passwordHasher.Fingerprint(command.Password);
        string requestHash = ComputeRequestHash(
            command,
            passwordFingerprint);
        string idempotencyKeyHash = Sha256(
            string.Concat(
                actor.TenantId.ToString("N"),
                "|",
                idempotencyKey));
        ShareIdempotencyRecord? idempotency =
            await _store.FindIdempotencyAsync(
                idempotencyKeyHash,
                cancellationToken);
        if (idempotency is not null)
        {
            ShareRecord? replayed = await _store.FindByIdAsync(
                idempotency.ShareId,
                cancellationToken);
            return idempotency.RequestHash == requestHash &&
                replayed is not null
                ? new(
                    ShareCreateStatus.TokenAlreadyIssued,
                    replayed,
                    null,
                    "share_token_already_issued")
                : new(
                    ShareCreateStatus.IdempotencyConflict,
                    null,
                    null,
                    "idempotency_key_conflict");
        }

        IReadOnlyList<ShareAssetSnapshot>? capturedAssets =
            await _assetCatalog.CaptureSnapshotAsync(
                actor.TenantId,
                command.TargetType,
                command.AlbumId,
                command.SnapshotAssets,
                cancellationToken);
        if (capturedAssets is null ||
            capturedAssets.Count == 0 ||
            capturedAssets.Count > 200 ||
            capturedAssets.Any(asset => !IsValidSnapshot(asset)))
        {
            await AuditAsync(
                ShareAuditAction.CreateRejected,
                actor.TenantId,
                null,
                actor.ActorId,
                "share_target_not_found",
                now);
            return new(
                ShareCreateStatus.NotFound,
                null,
                null,
                "share_target_not_found");
        }

        ShareAssetSnapshot[] assets = capturedAssets
            .Select(asset => asset with
            {
                Description =
                    command.MetadataExposure == ShareMetadataExposure.Basic
                        ? asset.Description
                        : null,
                CapturedAtUtc =
                    command.MetadataExposure == ShareMetadataExposure.Basic
                        ? asset.CapturedAtUtc
                        : null,
                Renditions = asset.Renditions.ToArray(),
            })
            .ToArray();
        // A share whose members expose nothing under the requested permissions
        // would publish an empty gallery, so it fails here rather than
        // succeeding hollow. An album target is not chosen asset by asset, so a
        // member still waiting for its derivatives is dropped instead of
        // failing the whole album.
        if (command.TargetType == ShareTargetType.Album)
        {
            assets = [.. assets.Where(asset => IsDeliverable(
                command.Permissions,
                asset))];
        }

        if (assets.Length == 0 ||
            assets.Any(asset => !IsDeliverable(command.Permissions, asset)))
        {
            await AuditAsync(
                ShareAuditAction.CreateRejected,
                actor.TenantId,
                null,
                actor.ActorId,
                "share_target_not_deliverable",
                now);
            return new(
                ShareCreateStatus.Invalid,
                null,
                null,
                "share_target_not_deliverable");
        }
        ShareSecretMaterial token = _tokenProtector.Issue();
        string? passwordHash = command.Password is null
            ? null
            : _passwordHasher.Hash(command.Password);
        Guid shareId = _idGenerator.NewId();
        EnsureUuid7(shareId);
        var record = new ShareRecord(
            shareId,
            actor.TenantId,
            actor.ActorId,
            command.Name.Trim(),
            command.TargetType,
            command.AlbumId,
            CopyAssets(assets),
            command.Permissions,
            command.MetadataExposure,
            token.PepperVersionId,
            token.DigestHex,
            passwordHash,
            now,
            command.ExpiresAtUtc,
            null,
            null,
            1,
            requestHash);
        ShareAddResult stored = await _store.AddAsync(
            record,
            idempotencyKeyHash,
            requestHash,
            cancellationToken);
        if (stored.Status == ShareAddStatus.IdempotencyConflict)
        {
            return new(
                ShareCreateStatus.IdempotencyConflict,
                null,
                null,
                "idempotency_key_conflict");
        }

        if (stored.Status == ShareAddStatus.Replayed)
        {
            return new(
                ShareCreateStatus.TokenAlreadyIssued,
                stored.Share,
                null,
                "share_token_already_issued");
        }

        await AuditAsync(
            ShareAuditAction.Created,
            actor.TenantId,
            record.Id,
            actor.ActorId,
            null,
            now);
        return new(ShareCreateStatus.Created, record, token.Plaintext);
    }

    public async ValueTask<ShareReadResult> GetAsync(
        ShareActor actor,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        cancellationToken.ThrowIfCancellationRequested();
        ShareRecord? share = await _store.FindAsync(
            actor.TenantId,
            shareId,
            cancellationToken);
        return share is null
            ? new(ShareReadStatus.NotFound, null)
            : new(ShareReadStatus.Found, share);
    }

    public async ValueTask<SharePageResult<ShareRecord>> ListAsync(
        ShareActor actor,
        int limit,
        string? status,
        string? cursor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (status is not null &&
            !string.Equals(status, "active", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "expired", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "revoked", StringComparison.OrdinalIgnoreCase))
        {
            return new(SharePageStatus.InvalidQuery, null);
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        DateTimeOffset? beforeCreatedAt = null;
        Guid? beforeId = null;
        if (cursor is not null)
        {
            if (!_cursorProtector.TryUnprotect(
                    cursor,
                    out ShareCursorState cursorState) ||
                cursorState.Kind != ShareCursorKind.Managed ||
                cursorState.TenantId != actor.TenantId ||
                cursorState.ShareId.HasValue ||
                cursorState.ShareVersion.HasValue ||
                cursorState.ExpiresAtUtc <= now ||
                !string.Equals(
                    cursorState.Status,
                    status,
                    StringComparison.OrdinalIgnoreCase) ||
                !cursorState.LastCreatedAtUtc.HasValue ||
                !cursorState.LastId.HasValue)
            {
                return new(SharePageStatus.InvalidCursor, null);
            }

            beforeCreatedAt = cursorState.LastCreatedAtUtc;
            beforeId = cursorState.LastId;
        }

        IReadOnlyList<ShareRecord> records = await _store.ListAsync(
            actor.TenantId,
            checked(limit + 1),
            status,
            now,
            beforeCreatedAt,
            beforeId,
            cancellationToken);
        ShareRecord[] items = records.Take(limit).ToArray();
        string? nextCursor = records.Count > limit
            ? _cursorProtector.Protect(
                new ShareCursorState(
                    ShareCursorKind.Managed,
                    actor.TenantId,
                    null,
                    null,
                    status,
                    items[^1].CreatedAtUtc,
                    items[^1].Id,
                    0,
                    now.AddMinutes(15)))
            : null;
        return new(
            SharePageStatus.Available,
            new SharePage<ShareRecord>(items, nextCursor));
    }

    public async ValueTask<ShareMutationResult> UpdateAsync(
        ShareActor actor,
        Guid shareId,
        long expectedVersion,
        ShareUpdateCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        ShareRecord? existing = await _store.FindAsync(
            actor.TenantId,
            shareId,
            cancellationToken);
        if (existing is null)
        {
            return new(ShareMutationStatus.NotFound, null);
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        string? name = command.Name?.Trim();
        if (!ValidIdempotencyKey(idempotencyKey) ||
            name is { Length: 0 or > 200 } ||
            command.Permissions is { } permissions && !ValidAccess(permissions) ||
            existing.MetadataExposure == ShareMetadataExposure.None &&
                command.MetadataExposure == ShareMetadataExposure.Basic ||
            command.ExpiresAtUtc is { } expiry &&
                (expiry.Offset != TimeSpan.Zero || expiry <= now))
        {
            return new(ShareMutationStatus.Invalid, existing, "share_update_invalid");
        }

        string idempotencyKeyHash =
            MutationKeyHash(actor, shareId, "update", idempotencyKey);
        string requestHash = ComputeUpdateRequestHash(command);
        ShareMutationResult? replay = await ResolveMutationReplayAsync(
            existing,
            idempotencyKeyHash,
            requestHash,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        if (existing.Version != expectedVersion)
        {
            return new(ShareMutationStatus.VersionConflict, existing);
        }

        string updatedName = name ?? existing.Name;
        ShareAccess updatedPermissions =
            command.Permissions ?? existing.Permissions;
        ShareMetadataExposure updatedExposure =
            command.MetadataExposure ?? existing.MetadataExposure;
        DateTimeOffset? updatedExpiry = command.SetExpiry
            ? command.ExpiresAtUtc
            : existing.ExpiresAtUtc;
        if (existing.Name == updatedName &&
            existing.Permissions == updatedPermissions &&
            existing.MetadataExposure == updatedExposure &&
            existing.ExpiresAtUtc == updatedExpiry)
        {
            ShareUpdateResult noOp = await _store.UpdateAsync(
                existing,
                expectedVersion,
                idempotencyKeyHash,
                requestHash,
                cancellationToken);
            return noOp.Status switch
            {
                ShareUpdateStatus.Unchanged =>
                    new(ShareMutationStatus.Unchanged, existing),
                ShareUpdateStatus.Replayed =>
                    new(ShareMutationStatus.Replayed, noOp.Share),
                ShareUpdateStatus.IdempotencyConflict =>
                    new(ShareMutationStatus.IdempotencyConflict, existing),
                ShareUpdateStatus.NotFound =>
                    new(ShareMutationStatus.NotFound, null),
                _ => new(ShareMutationStatus.VersionConflict, existing),
            };
        }

        ShareRecord updated = existing with
        {
            Name = updatedName,
            Permissions = updatedPermissions,
            MetadataExposure = updatedExposure,
            ExpiresAtUtc = updatedExpiry,
            Version = checked(existing.Version + 1),
        };

        ShareUpdateResult result = await _store.UpdateAsync(
            updated,
            expectedVersion,
            idempotencyKeyHash,
            requestHash,
            cancellationToken);
        if (result.Status == ShareUpdateStatus.Replayed)
        {
            return new(ShareMutationStatus.Replayed, result.Share);
        }

        if (result.Status != ShareUpdateStatus.Updated)
        {
            return new(
                result.Status switch
                {
                    ShareUpdateStatus.NotFound => ShareMutationStatus.NotFound,
                    ShareUpdateStatus.IdempotencyConflict =>
                        ShareMutationStatus.IdempotencyConflict,
                    ShareUpdateStatus.Unchanged =>
                        ShareMutationStatus.Unchanged,
                    _ => ShareMutationStatus.VersionConflict,
                },
                result.Share);
        }

        await AuditAsync(
            ShareAuditAction.Updated,
            actor.TenantId,
            shareId,
            actor.ActorId,
            null,
            now);
        return new(ShareMutationStatus.Updated, result.Share);
    }

    public async ValueTask<ShareMutationResult> RevokeAsync(
        ShareActor actor,
        Guid shareId,
        long expectedVersion,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        cancellationToken.ThrowIfCancellationRequested();
        ShareRecord? existing = await _store.FindAsync(
            actor.TenantId,
            shareId,
            cancellationToken);
        if (existing is null)
        {
            return new(ShareMutationStatus.NotFound, null);
        }

        if (!ValidIdempotencyKey(idempotencyKey))
        {
            return new(
                ShareMutationStatus.Invalid,
                existing,
                "invalid_idempotency_key");
        }

        string idempotencyKeyHash =
            MutationKeyHash(actor, shareId, "revoke", idempotencyKey);
        string requestHash = Sha256("revoke");
        ShareMutationResult? replay = await ResolveMutationReplayAsync(
            existing,
            idempotencyKeyHash,
            requestHash,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        if (existing.Version != expectedVersion)
        {
            return new(ShareMutationStatus.VersionConflict, existing);
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        ShareRecord updated = existing.RevokedAtUtc.HasValue
            ? existing
            : existing with
            {
                RevokedAtUtc = now,
                RevokedByActorId = actor.ActorId,
                Version = checked(existing.Version + 1),
            };
        ShareUpdateResult result = await _store.UpdateAsync(
            updated,
            expectedVersion,
            idempotencyKeyHash,
            requestHash,
            cancellationToken);
        if (result.Status == ShareUpdateStatus.Replayed)
        {
            return new(ShareMutationStatus.Replayed, result.Share);
        }

        if (result.Status != ShareUpdateStatus.Updated)
        {
            return new(
                result.Status switch
                {
                    ShareUpdateStatus.NotFound => ShareMutationStatus.NotFound,
                    ShareUpdateStatus.IdempotencyConflict =>
                        ShareMutationStatus.IdempotencyConflict,
                    ShareUpdateStatus.Unchanged =>
                        ShareMutationStatus.Unchanged,
                    _ => ShareMutationStatus.VersionConflict,
                },
                result.Share);
        }

        await AuditAsync(
            ShareAuditAction.Revoked,
            actor.TenantId,
            shareId,
            actor.ActorId,
            null,
            now);
        return new(ShareMutationStatus.Updated, result.Share);
    }

    public async ValueTask<SharePublicResult> GetPublicAsync(
        string? publicToken,
        string? sessionToken,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 200)
        {
            return new(SharePublicStatus.NotFound, null, "share_not_found");
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        (ShareRecord? Share, bool SessionAuthenticated) resolved =
            await ResolvePublicAsync(
                publicToken,
                sessionToken,
                now,
                cancellationToken);
        if (resolved.Share is null)
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                null,
                null,
                null,
                "share_not_found",
                now);
            return new(SharePublicStatus.NotFound, null, "share_not_found");
        }

        ShareRecord share = resolved.Share;
        if (share.StatusAt(now) != ShareLifecycleStatus.Active)
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                share.TenantId,
                share.Id,
                null,
                "share_gone",
                now);
            return new(SharePublicStatus.Gone, null, "share_gone");
        }

        int offset = 0;
        if (cursor is not null)
        {
            if (!_cursorProtector.TryUnprotect(
                    cursor,
                    out ShareCursorState cursorState) ||
                cursorState.Kind != ShareCursorKind.PublicAssets ||
                cursorState.TenantId != share.TenantId ||
                cursorState.ShareId != share.Id ||
                cursorState.ShareVersion != share.Version ||
                cursorState.ExpiresAtUtc <= now ||
                cursorState.Offset < 0 ||
                cursorState.Offset > share.Assets.Count)
            {
                return new(
                    SharePublicStatus.NotFound,
                    null,
                    "share_cursor_invalid");
            }

            offset = cursorState.Offset;
        }

        bool passwordRequired =
            share.PasswordProtected && !resolved.SessionAuthenticated;
        ShareAssetSnapshot[] availableAssets = passwordRequired
            ? []
            : ApplyExposure(share).Skip(offset).Take(limit + 1).ToArray();
        ShareAssetSnapshot[] assets =
            availableAssets.Take(limit).ToArray();
        string? nextCursor =
            !passwordRequired && availableAssets.Length > limit
                ? _cursorProtector.Protect(
                    new ShareCursorState(
                        ShareCursorKind.PublicAssets,
                        share.TenantId,
                        share.Id,
                        share.Version,
                        null,
                        null,
                        null,
                        offset + limit,
                        now.AddMinutes(15)))
                : null;
        var projection = new SharePublicProjection(
            share.Id,
            share.Name,
            ShareLifecycleStatus.Active,
            share.Permissions,
            share.MetadataExposure,
            passwordRequired,
            share.ExpiresAtUtc,
            assets,
            nextCursor);
        await AuditAsync(
            ShareAuditAction.Viewed,
            share.TenantId,
            share.Id,
            null,
            null,
            now);
        return new(SharePublicStatus.Available, projection);
    }

    /// <summary>
    /// Resolves one captured rendition for an anonymous share recipient. This
    /// owns the token, session, password, lifecycle, and snapshot membership
    /// decisions; the delivery grant authorization port owns the resource
    /// decision that follows. Every concealed reason reports the same status so
    /// the byte surface cannot be used to probe a share.
    /// </summary>
    public async ValueTask<ShareRenditionResult> ResolvePublicRenditionAsync(
        string? publicToken,
        string? sessionToken,
        Guid assetId,
        string? deliveryIdentifier,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        (ShareRecord? Share, bool SessionAuthenticated) resolved =
            await ResolvePublicAsync(
                publicToken,
                sessionToken,
                now,
                cancellationToken);
        if (resolved.Share is null)
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                null,
                null,
                null,
                "share_not_found",
                now);
            return new(
                ShareRenditionStatus.NotFound,
                null,
                "share_rendition_not_found");
        }

        ShareRecord share = resolved.Share;
        if (share.StatusAt(now) != ShareLifecycleStatus.Active)
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                share.TenantId,
                share.Id,
                null,
                "share_gone",
                now);
            return new(ShareRenditionStatus.Gone, null, "share_gone");
        }

        if (share.PasswordProtected && !resolved.SessionAuthenticated)
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                share.TenantId,
                share.Id,
                null,
                "share_password_required",
                now);
            return new(
                ShareRenditionStatus.NotFound,
                null,
                "share_rendition_not_found");
        }

        ShareAssetSnapshot? asset = share.Assets.SingleOrDefault(candidate =>
            candidate.AssetId == assetId);
        ShareRendition? rendition = asset?.Renditions.SingleOrDefault(candidate =>
            candidate.DeliveryIdentifier is not null &&
            string.Equals(
                candidate.DeliveryIdentifier,
                deliveryIdentifier,
                StringComparison.Ordinal));
        if (asset is null ||
            rendition is null ||
            !IsExposed(share.Permissions, rendition))
        {
            await AuditAsync(
                ShareAuditAction.ViewRejected,
                share.TenantId,
                share.Id,
                null,
                "share_rendition_not_found",
                now);
            return new(
                ShareRenditionStatus.NotFound,
                null,
                "share_rendition_not_found");
        }

        return new(
            ShareRenditionStatus.Available,
            new ShareRenditionTarget(
                share.TenantId,
                share.Id,
                share.Version,
                asset.AssetId,
                asset.RevisionId,
                rendition.DeliveryIdentifier!,
                rendition.RequiredAccess));
    }

    public async ValueTask<ShareChallengeResult> ChallengeAsync(
        string? publicToken,
        string password,
        string rateLimitPartition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        if (!_tokenProtector.TryDigest(
                publicToken,
                out string pepperVersionId,
                out string digestHex))
        {
            await _rateLimiter.TryAcquireAsync(
                Sha256(string.Concat("invalid|", rateLimitPartition)),
                now,
                _options.ChallengeWindow,
                _options.ChallengeLimit,
                cancellationToken);
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                null,
                null,
                null,
                "share_not_found",
                now);
            return new(ShareChallengeStatus.NotFound, null, null);
        }

        if (password.Length is < 1 or > 256)
        {
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                null,
                null,
                null,
                "share_password_invalid",
                now);
            return new(ShareChallengeStatus.InvalidPassword, null, null);
        }

        ShareRecord? share = await _store.FindByTokenDigestAsync(
            pepperVersionId,
            digestHex,
            cancellationToken);
        string rateLimitKey = Sha256(
            share is null
                ? string.Concat("invalid|", rateLimitPartition)
                : string.Concat(
                    share.Id.ToString("N"),
                    "|",
                    rateLimitPartition));
        ShareRateLimitDecision limit = await _rateLimiter.TryAcquireAsync(
            rateLimitKey,
            now,
            _options.ChallengeWindow,
            _options.ChallengeLimit,
            cancellationToken);
        if (!limit.IsAllowed)
        {
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                null,
                null,
                null,
                "share_challenge_rate_limited",
                now);
            return new(
                ShareChallengeStatus.RateLimited,
                null,
                null,
                limit.RetryAfter);
        }

        if (share is null)
        {
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                null,
                null,
                null,
                "share_not_found",
                now);
            return new(ShareChallengeStatus.NotFound, null, null);
        }

        if (share.StatusAt(now) != ShareLifecycleStatus.Active)
        {
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                share.TenantId,
                share.Id,
                null,
                "share_gone",
                now);
            return new(ShareChallengeStatus.Gone, null, null);
        }

        if (share.PasswordHash is not null &&
            !_passwordHasher.Verify(share.PasswordHash, password))
        {
            await AuditAsync(
                ShareAuditAction.ChallengeRejected,
                share.TenantId,
                share.Id,
                null,
                "share_password_invalid",
                now);
            return new(ShareChallengeStatus.InvalidPassword, null, null);
        }

        DateTimeOffset expiresAt = now.Add(_options.SessionLifetime);
        if (share.ExpiresAtUtc is { } shareExpiry && expiresAt > shareExpiry)
        {
            expiresAt = shareExpiry;
        }

        ShareSecretMaterial sessionSecret = _sessionProtector.Issue();
        Guid sessionId = _idGenerator.NewId();
        EnsureUuid7(sessionId);
        await _store.AddSessionAsync(
            new ShareSessionRecord(
                sessionId,
                share.TenantId,
                share.Id,
                share.Version,
                sessionSecret.PepperVersionId,
                sessionSecret.DigestHex,
                now,
                expiresAt),
            cancellationToken);
        await AuditAsync(
            ShareAuditAction.Challenged,
            share.TenantId,
            share.Id,
            null,
            null,
            now);
        return new(
            ShareChallengeStatus.Authenticated,
            sessionSecret.Plaintext,
            expiresAt);
    }

    public async ValueTask AuditChallengeRejectionAsync(
        string? publicToken,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        ShareRecord? share = null;
        if (_tokenProtector.TryDigest(
                publicToken,
                out string pepperVersionId,
                out string digestHex))
        {
            share = await _store.FindByTokenDigestAsync(
                pepperVersionId,
                digestHex,
                cancellationToken);
        }

        await AuditAsync(
            ShareAuditAction.ChallengeRejected,
            share?.TenantId,
            share?.Id,
            null,
            reasonCode,
            now);
    }

    private async ValueTask<(ShareRecord? Share, bool SessionAuthenticated)>
        ResolvePublicAsync(
            string? publicToken,
            string? sessionToken,
            DateTimeOffset now,
            CancellationToken cancellationToken)
    {
        ShareSessionRecord? session = null;
        ShareRecord? sessionShare = null;
        if (_sessionProtector.TryDigest(
                sessionToken,
                out string sessionPepper,
                out string sessionDigest))
        {
            session = await _store.FindSessionAsync(
                sessionPepper,
                sessionDigest,
                now,
                cancellationToken);
            if (session is not null && now < session.ExpiresAtUtc)
            {
                sessionShare = await _store.FindByIdAsync(
                    session.ShareId,
                    cancellationToken);
            }
        }

        if (_tokenProtector.TryDigest(
                publicToken,
                out string tokenPepper,
                out string tokenDigest))
        {
            ShareRecord? tokenShare = await _store.FindByTokenDigestAsync(
                tokenPepper,
                tokenDigest,
                cancellationToken);
            if (tokenShare is null)
            {
                return (null, false);
            }

            bool authenticated =
                session is not null &&
                sessionShare is not null &&
                sessionShare.Id == tokenShare.Id &&
                sessionShare.TenantId == session.TenantId &&
                sessionShare.Version == session.ShareVersion;
            return (tokenShare, authenticated);
        }

        return session is not null &&
            sessionShare is not null &&
            sessionShare.TenantId == session.TenantId
            ? (
                sessionShare,
                sessionShare.Version == session.ShareVersion
            )
            : (null, false);
    }

    private static IEnumerable<ShareAssetSnapshot> ApplyExposure(
        ShareRecord share)
    {
        foreach (ShareAssetSnapshot asset in share.Assets)
        {
            IEnumerable<ShareRendition> renditions = asset.Renditions.Where(
                rendition => IsExposed(share.Permissions, rendition));
            yield return asset with
            {
                Description = share.MetadataExposure == ShareMetadataExposure.Basic
                    ? asset.Description
                    : null,
                CapturedAtUtc = share.MetadataExposure == ShareMetadataExposure.Basic
                    ? asset.CapturedAtUtc
                    : null,
                Renditions = renditions.ToArray(),
            };
        }
    }

    /// <summary>
    /// A rendition is exposed when the share grants the access it demands.
    /// Viewing is implied by every share, so display renditions stay visible
    /// while download renditions follow the download permission.
    /// </summary>
    private static bool IsExposed(
        ShareAccess permissions,
        ShareRendition rendition) =>
        rendition.RequiredAccess == ShareAccess.View ||
        permissions.HasFlag(rendition.RequiredAccess);

    /// <summary>
    /// An asset is deliverable when the share would publish at least one
    /// rendition a recipient can actually fetch.
    /// </summary>
    private static bool IsDeliverable(
        ShareAccess permissions,
        ShareAssetSnapshot asset) =>
        asset.Renditions.Any(rendition => IsExposed(permissions, rendition));

    private static string? ValidateCreate(
        ShareCreateCommand command,
        string idempotencyKey,
        DateTimeOffset now)
    {
        string name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200 ||
            !ValidIdempotencyKey(idempotencyKey) ||
            !ValidAccess(command.Permissions) ||
            !Enum.IsDefined(command.MetadataExposure) ||
            !Enum.IsDefined(command.TargetType) ||
            command.ExpiresAtUtc is { } expiry &&
                (expiry.Offset != TimeSpan.Zero || expiry <= now) ||
            command.Password is { Length: > 256 } ||
            command.Password is not null && command.Password.Length < 8)
        {
            return "share_request_invalid";
        }

        bool validTarget = command.TargetType switch
        {
            ShareTargetType.Album =>
                command.AlbumId is { } albumId &&
                albumId != Guid.Empty &&
                albumId.Version == 7 &&
                command.SnapshotAssets.Count == 0,
            ShareTargetType.Snapshot =>
                command.AlbumId is null &&
                command.SnapshotAssets.Count is > 0 and <= 200 &&
                command.SnapshotAssets.Select(item => item.AssetId).Distinct().Count() ==
                command.SnapshotAssets.Count,
            _ => false,
        };
        return validTarget ? null : "share_target_invalid";
    }

    private static bool ValidAccess(ShareAccess access) =>
        (access & ~AllAccess) == ShareAccess.None &&
        access.HasFlag(ShareAccess.View);

    private static bool ValidIdempotencyKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':');

    private static bool IsValidSnapshot(ShareAssetSnapshot asset) =>
        asset.AssetId != Guid.Empty &&
        asset.AssetId.Version == 7 &&
        asset.RevisionId != Guid.Empty &&
        asset.RevisionId.Version == 7 &&
        asset.RevisionNumber > 0 &&
        asset.AssetVersion > 0 &&
        !string.IsNullOrWhiteSpace(asset.Title) &&
        asset.Width > 0 &&
        asset.Height > 0 &&
        asset.Renditions.All(rendition =>
            rendition.Path.StartsWith('/') &&
            !rendition.Path.Contains("://", StringComparison.Ordinal) &&
            rendition.Width > 0 &&
            rendition.Height > 0 &&
            (rendition.RequiredAccess is
                ShareAccess.View or
                ShareAccess.DownloadRenditions or
                ShareAccess.DownloadOriginal) &&
            (rendition.DeliveryIdentifier is null ||
                IsDeliveryIdentifier(rendition.DeliveryIdentifier)));

    /// <summary>
    /// Matches the identifier grammar the delivery grant resource accepts so a
    /// captured rendition can always be authorized for delivery later.
    /// </summary>
    private static bool IsDeliveryIdentifier(string value) =>
        value.Length is > 0 and <= 256 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':');

    private static ShareAssetSnapshot[] CopyAssets(
        IReadOnlyList<ShareAssetSnapshot> assets) =>
        assets.Select(asset => asset with
        {
            Renditions = asset.Renditions.ToArray(),
        }).ToArray();

    private static string ComputeRequestHash(
        ShareCreateCommand command,
        string? passwordFingerprint)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Name = command.Name.Trim(),
            command.TargetType,
            command.AlbumId,
            Assets = command.SnapshotAssets.Select(asset => new
            {
                asset.AssetId,
                asset.Version,
            }),
            command.Permissions,
            command.MetadataExposure,
            command.ExpiresAtUtc,
            PasswordFingerprint = passwordFingerprint,
        });
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<ShareMutationResult?> ResolveMutationReplayAsync(
        ShareRecord existing,
        string idempotencyKeyHash,
        string requestHash,
        CancellationToken cancellationToken)
    {
        ShareIdempotencyRecord? idempotency =
            await _store.FindIdempotencyAsync(
                idempotencyKeyHash,
                cancellationToken);
        if (idempotency is null)
        {
            return null;
        }

        return idempotency.ShareId == existing.Id &&
            idempotency.RequestHash == requestHash
            ? new(ShareMutationStatus.Replayed, existing)
            : new(ShareMutationStatus.IdempotencyConflict, existing);
    }

    private static string ComputeUpdateRequestHash(
        ShareUpdateCommand command)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Operation = "update",
            Name = command.Name?.Trim(),
            command.Permissions,
            command.MetadataExposure,
            command.ExpiresAtUtc,
            command.SetExpiry,
        });
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string MutationKeyHash(
        ShareActor actor,
        Guid shareId,
        string operation,
        string idempotencyKey) =>
        Sha256(string.Concat(
            actor.TenantId.ToString("N"),
            "|",
            shareId.ToString("N"),
            "|",
            operation,
            "|",
            idempotencyKey));

    private static string Sha256(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask AuditAsync(
        ShareAuditAction action,
        Guid? tenantId,
        Guid? shareId,
        Guid? actorId,
        string? reasonCode,
        DateTimeOffset now)
    {
        try
        {
            await _audit.WriteAsync(
                new ShareAuditEvent(
                    action,
                    tenantId,
                    shareId,
                    actorId,
                    reasonCode,
                    now),
                CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero
            ? value
            : throw new InvalidOperationException("The sharing clock must return UTC.");

    private static void EnsureUuid7(Guid value)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new InvalidOperationException(
                "The sharing identifier generator must return UUIDv7.");
        }
    }
}
