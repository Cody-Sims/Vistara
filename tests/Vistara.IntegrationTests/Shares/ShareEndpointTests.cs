using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Features.Shares;
using Vistara.Application.Common;
using Vistara.Application.Sharing;
using Vistara.Auth.Sharing;
using Xunit;

namespace Vistara.IntegrationTests.Shares;

public sealed class ShareEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7(Now);
    private static readonly Guid ActorId = Guid.CreateVersion7(Now.AddMilliseconds(1));
    private static readonly Guid ShareId =
        Guid.Parse("0da03ec4-5280-7df5-b69b-9365bd639dcc");
    private static readonly Guid AssetId = Guid.CreateVersion7(Now.AddMilliseconds(2));
    private static readonly Guid RevisionId = Guid.CreateVersion7(Now.AddMilliseconds(3));

    [Fact]
    public async Task Shares_create_endpoint_requires_idempotency_and_returns_token_once()
    {
        var store = new EndpointStore();
        ShareService service = CreateService(store);
        var authorization = new FixedAuthorizationPort(ShareAccessDecision.Authorized(
            TenantId,
            ActorId));
        DefaultHttpContext missingKey = JsonContext(
            """
            {
              "name": "Private gallery",
              "targetKind": "snapshot",
              "snapshotAssets": [{ "id": "0196f001-0000-7000-8000-000000000001", "version": 4 }],
              "permissions": { "view": true, "downloadRenditions": false, "downloadOriginal": false },
              "metadataExposure": "basic"
            }
            """);

        await ShareEndpoint.CreateAsync(
            missingKey,
            authorization,
            service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, missingKey.Response.StatusCode);

        DefaultHttpContext created = JsonContext(
            $$"""
            {
              "name": "Private gallery",
              "targetKind": "snapshot",
              "snapshotAssets": [{ "id": "{{AssetId}}", "version": 4 }],
              "permissions": { "view": true, "downloadRenditions": false, "downloadOriginal": false },
              "metadataExposure": "basic"
            }
            """);
        created.Request.Headers["Idempotency-Key"] = "endpoint-create";
        await ShareEndpoint.CreateAsync(
            created,
            authorization,
            service,
            CancellationToken.None);
        string firstBody = await ReadBodyAsync(created);
        string publicToken = JsonDocument.Parse(firstBody)
            .RootElement.GetProperty("publicToken").GetString()!;

        DefaultHttpContext replay = JsonContext(
            $$"""
            {
              "name": "Private gallery",
              "targetKind": "snapshot",
              "snapshotAssets": [{ "id": "{{AssetId}}", "version": 4 }],
              "permissions": { "view": true, "downloadRenditions": false, "downloadOriginal": false },
              "metadataExposure": "basic"
            }
            """);
        replay.Request.Headers["Idempotency-Key"] = "endpoint-create";
        await ShareEndpoint.CreateAsync(
            replay,
            authorization,
            service,
            CancellationToken.None);
        string replayBody = await ReadBodyAsync(replay);

        Assert.Equal(StatusCodes.Status201Created, created.Response.StatusCode);
        Assert.Equal("\"v1\"", created.Response.Headers.ETag);
        Assert.StartsWith("vsh_", publicToken, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status409Conflict, replay.Response.StatusCode);
        Assert.DoesNotContain(publicToken, replayBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shares_patch_requires_etag_and_revoke_is_idempotent()
    {
        var store = new EndpointStore();
        ShareService service = CreateService(store);
        ShareCreateResult created = await service.CreateAsync(
            new ShareActor(TenantId, ActorId),
            Command(),
            "etag-create",
            CancellationToken.None);
        var authorization = new FixedAuthorizationPort(ShareAccessDecision.Authorized(
            TenantId,
            ActorId));
        DefaultHttpContext missing = JsonContext("""{ "name": "Renamed" }""");

        await ShareEndpoint.UpdateAsync(
            missing,
            ShareId,
            authorization,
            service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status428PreconditionRequired, missing.Response.StatusCode);

        DefaultHttpContext update = JsonContext("""{ "name": "Renamed" }""");
        update.Request.Headers.IfMatch = "\"v1\"";
        update.Request.Headers["Idempotency-Key"] = "update-1";
        await ShareEndpoint.UpdateAsync(
            update,
            ShareId,
            authorization,
            service,
            CancellationToken.None);
        DefaultHttpContext replayedUpdate = JsonContext("""{ "name": "Renamed" }""");
        replayedUpdate.Request.Headers.IfMatch = "\"v1\"";
        replayedUpdate.Request.Headers["Idempotency-Key"] = "update-1";
        await ShareEndpoint.UpdateAsync(
            replayedUpdate,
            ShareId,
            authorization,
            service,
            CancellationToken.None);

        DefaultHttpContext revoke = EmptyContext();
        revoke.Request.Headers.IfMatch = "\"v2\"";
        revoke.Request.Headers["Idempotency-Key"] = "revoke-1";
        await ShareEndpoint.RevokeAsync(
            revoke,
            ShareId,
            authorization,
            service,
            CancellationToken.None);
        DefaultHttpContext repeated = EmptyContext();
        repeated.Request.Headers.IfMatch = "\"v2\"";
        repeated.Request.Headers["Idempotency-Key"] = "revoke-1";
        await ShareEndpoint.RevokeAsync(
            repeated,
            ShareId,
            authorization,
            service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, update.Response.StatusCode);
        Assert.Equal("\"v2\"", update.Response.Headers.ETag);
        Assert.Equal(StatusCodes.Status200OK, replayedUpdate.Response.StatusCode);
        Assert.Equal("true", replayedUpdate.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal("\"v2\"", replayedUpdate.Response.Headers.ETag);
        Assert.Equal(StatusCodes.Status204NoContent, revoke.Response.StatusCode);
        Assert.Equal(StatusCodes.Status204NoContent, repeated.Response.StatusCode);
        Assert.Equal("true", repeated.Response.Headers["Idempotency-Replayed"]);
        Assert.Equal(ShareLifecycleStatus.Revoked, store.Record!.StatusAt(Now));
        Assert.NotNull(created.PublicToken);
    }

    [Fact]
    public async Task Shares_public_challenge_uses_cookie_or_header_and_returns_gone()
    {
        var clock = new EndpointClock(Now);
        var store = new EndpointStore();
        ShareService service = CreateService(store, clock);
        ShareCreateResult created = await service.CreateAsync(
            new ShareActor(TenantId, ActorId),
            Command(password: "correct horse battery staple"),
            "challenge-create",
            CancellationToken.None);
        DefaultHttpContext before = EmptyContext();

        await ShareEndpoint.GetPublicAsync(
            before,
            created.PublicToken!,
            service,
            CancellationToken.None);
        string beforeBody = await ReadBodyAsync(before);

        DefaultHttpContext challenge = JsonContext(
            """{ "password": "correct horse battery staple" }""");
        await ShareEndpoint.ChallengeAsync(
            challenge,
            created.PublicToken!,
            service,
            CancellationToken.None);
        _ = await ReadBodyAsync(challenge);
        string setCookie = challenge.Response.Headers.SetCookie.ToString();
        string session = setCookie.Split(';', 2)[0].Split('=', 2)[1];

        DefaultHttpContext after = EmptyContext();
        after.Request.Headers["X-Vistara-Share-Session"] = session;
        await ShareEndpoint.GetPublicAsync(
            after,
            "not-presented-again",
            service,
            CancellationToken.None);
        string afterBody = await ReadBodyAsync(after);

        Assert.Equal(StatusCodes.Status200OK, before.Response.StatusCode);
        Assert.True(JsonDocument.Parse(beforeBody)
            .RootElement.GetProperty("passwordRequired").GetBoolean());
        Assert.Contains("Vistara.ShareSession=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(JsonDocument.Parse(afterBody)
            .RootElement.GetProperty("passwordRequired").GetBoolean());
        Assert.True(JsonDocument.Parse(afterBody).RootElement.TryGetProperty("assets", out _));

        ShareMutationResult revoked = await service.RevokeAsync(
            new ShareActor(TenantId, ActorId),
            ShareId,
            store.Record!.Version,
            "public-revoke",
            CancellationToken.None);
        DefaultHttpContext gone = EmptyContext();
        await ShareEndpoint.GetPublicAsync(
            gone,
            created.PublicToken!,
            service,
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status410Gone, gone.Response.StatusCode);
        Assert.Equal(ShareMutationStatus.Updated, revoked.Status);
    }

    [Fact]
    public async Task Shares_reject_chunked_oversized_challenges_and_invalid_patch_permissions()
    {
        var store = new EndpointStore();
        ShareService service = CreateService(store);
        ShareCreateResult created = await service.CreateAsync(
            new ShareActor(TenantId, ActorId),
            Command(password: "correct horse battery staple"),
            "validation-create",
            CancellationToken.None);
        DefaultHttpContext oversized = EmptyContext();
        oversized.Request.ContentType = "application/json";
        oversized.Request.Body = new MemoryStream(
            Encoding.UTF8.GetBytes(
                $$"""{"password":"{{new string('a', 33_000)}}"}"""));

        await ShareEndpoint.ChallengeAsync(
            oversized,
            created.PublicToken!,
            service,
            CancellationToken.None);

        var authorization = new FixedAuthorizationPort(
            ShareAccessDecision.Authorized(TenantId, ActorId));
        DefaultHttpContext invalidPatch = JsonContext(
            """
            {
              "permissions": {
                "view": false,
                "downloadRenditions": false,
                "downloadOriginal": false
              }
            }
            """);
        invalidPatch.Request.Headers.IfMatch = "\"v1\"";
        invalidPatch.Request.Headers["Idempotency-Key"] = "invalid-patch";
        await ShareEndpoint.UpdateAsync(
            invalidPatch,
            ShareId,
            authorization,
            service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversized.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, invalidPatch.Response.StatusCode);
    }

    private static ShareService CreateService(
        EndpointStore store,
        EndpointClock? clock = null)
    {
        var random = new EndpointRandomSource();
        var peppers = new SharePepperSet(
            "v1",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)77, 32).ToArray(),
            });
        return new ShareService(
            clock ?? new EndpointClock(Now),
            new EndpointUuidGenerator(),
            new ShareTokenProtector(random, peppers),
            new Pbkdf2SharePasswordHasher(random, peppers, 10_000),
            new ShareSessionProtector(random, peppers),
            new ShareCursorProtector(peppers),
            store,
            new EndpointCatalog(),
            new InMemoryShareChallengeRateLimiter(),
            new EndpointAudit(),
            new ShareOptions(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(5), 5));
    }

    private static ShareCreateCommand Command(string? password = null) =>
        new(
            "Private gallery",
            ShareTargetType.Snapshot,
            null,
            [new ShareAssetReference(AssetId, 4)],
            ShareAccess.View,
            ShareMetadataExposure.Basic,
            null,
            password);

    private static DefaultHttpContext JsonContext(string json)
    {
        DefaultHttpContext context = EmptyContext();
        byte[] body = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        context.Request.ContentType = "application/json";
        return context;
    }

    private static DefaultHttpContext EmptyContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress =
            System.Net.IPAddress.Parse("198.51.100.20");
        return context;
    }

    private static async Task<string> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class FixedAuthorizationPort(ShareAccessDecision decision)
        : IShareAuthorizationPort
    {
        public ValueTask<ShareAccessDecision> AuthorizeAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(decision);
    }

    private sealed class EndpointClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class EndpointUuidGenerator : IUuid7Generator
    {
        private int _count;

        public Guid NewId() => _count++ == 0
            ? ShareId
            : Guid.CreateVersion7(Now.AddMilliseconds(100 + _count));
    }

    private sealed class EndpointRandomSource : IShareRandomSource
    {
        private byte _seed = 1;

        public void Fill(Span<byte> destination)
        {
            for (int index = 0; index < destination.Length; index++)
            {
                destination[index] = _seed++;
            }
        }
    }

    private sealed class EndpointCatalog : IShareAssetCatalog
    {
        public ValueTask<IReadOnlyList<ShareAssetSnapshot>?> CaptureSnapshotAsync(
            Guid tenantId,
            ShareTargetType targetType,
            Guid? albumId,
            IReadOnlyList<ShareAssetReference> assets,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ShareAssetSnapshot>?>(
                [
                    new ShareAssetSnapshot(
                        AssetId,
                        RevisionId,
                        4,
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
                                ShareAccess.View),
                        ]),
                ]);
    }

    private sealed class EndpointAudit : IShareAuditSink
    {
        public ValueTask WriteAsync(
            ShareAuditEvent auditEvent,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class EndpointStore : IShareStore
    {
        private string? _keyHash;
        private string? _requestHash;
        private readonly Dictionary<string, string> _mutations =
            new(StringComparer.Ordinal);
        private readonly List<ShareSessionRecord> _sessions = [];

        public ShareRecord? Record { get; private set; }

        public ValueTask<ShareIdempotencyRecord?> FindIdempotencyAsync(
            string idempotencyKeyHash,
            CancellationToken cancellationToken)
        {
            if (_keyHash == idempotencyKeyHash)
            {
                return ValueTask.FromResult<ShareIdempotencyRecord?>(
                    new ShareIdempotencyRecord(
                        Record!.Id,
                        _requestHash!));
            }

            return ValueTask.FromResult(
                _mutations.TryGetValue(
                    idempotencyKeyHash,
                    out string? requestHash)
                    ? new ShareIdempotencyRecord(Record!.Id, requestHash)
                    : null);
        }

        public ValueTask<ShareAddResult> AddAsync(
            ShareRecord share,
            string idempotencyKeyHash,
            string requestHash,
            CancellationToken cancellationToken)
        {
            if (_keyHash == idempotencyKeyHash)
            {
                return ValueTask.FromResult(
                    _requestHash == requestHash
                        ? ShareAddResult.Replayed(Record!)
                        : ShareAddResult.IdempotencyConflict());
            }

            Record = share;
            _keyHash = idempotencyKeyHash;
            _requestHash = requestHash;
            return ValueTask.FromResult(ShareAddResult.Created(share));
        }

        public ValueTask<ShareRecord?> FindAsync(
            Guid tenantId,
            Guid shareId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Record is { } record &&
                record.TenantId == tenantId &&
                record.Id == shareId
                    ? record
                    : null);

        public ValueTask<ShareRecord?> FindByIdAsync(
            Guid shareId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Record is { } record && record.Id == shareId
                    ? record
                    : null);

        public ValueTask<ShareRecord?> FindByTokenDigestAsync(
            string pepperVersionId,
            string digestHex,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Record is { } record &&
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
                Record is { } record && record.TenantId == tenantId
                    ? [record]
                    : []);

        public ValueTask<ShareUpdateResult> UpdateAsync(
            ShareRecord updated,
            long expectedVersion,
            string idempotencyKeyHash,
            string requestHash,
            CancellationToken cancellationToken)
        {
            if (_mutations.TryGetValue(
                    idempotencyKeyHash,
                    out string? existingHash))
            {
                return ValueTask.FromResult(
                    existingHash == requestHash
                        ? ShareUpdateResult.Replayed(Record!)
                        : ShareUpdateResult.IdempotencyConflict());
            }

            if (Record is null)
            {
                return ValueTask.FromResult(ShareUpdateResult.NotFound());
            }

            if (Record.Version != expectedVersion)
            {
                return ValueTask.FromResult(ShareUpdateResult.VersionConflict());
            }

            Record = updated;
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
            _sessions.Add(session);
            return ValueTask.CompletedTask;
        }

        public ValueTask<ShareSessionRecord?> FindSessionAsync(
            string pepperVersionId,
            string digestHex,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _sessions.SingleOrDefault(item =>
                    item.PepperVersionId == pepperVersionId &&
                    item.DigestHex == digestHex &&
                    item.ExpiresAtUtc > nowUtc));
    }
}
