using Vistara.Domain.Assets;
using Vistara.Domain.Uploads;

namespace Vistara.UnitTests.Uploads;

public sealed class UploadSessionTests
{
    private static readonly Guid TenantId = Guid.Parse("0198ef6d-b620-7000-8000-000000000001");
    private static readonly Guid ActorId = Guid.Parse("0198ef6d-b620-7000-8000-000000000002");
    private static readonly Guid SessionId = Guid.Parse("0198ef6d-b620-7000-8000-000000000003");
    private static readonly Guid ReservationId = Guid.Parse("0198ef6d-b620-7000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Uploads_commit_abort_and_expiry_are_idempotent()
    {
        UploadSession committed = Create(UploadStrategy.Direct);
        Assert.True(committed.Issue("provider-upload", Now.AddMinutes(1)).IsSuccess);
        Assert.True(committed.RequestCommit(Now.AddMinutes(2)).IsSuccess);
        long committedVersion = committed.Version;
        Assert.True(committed.RequestCommit(Now.AddMinutes(3)).IsSuccess);

        UploadSession aborted = Create(UploadStrategy.Proxy);
        Assert.True(aborted.Abort(Now.AddMinutes(1)).IsSuccess);
        long abortedVersion = aborted.Version;
        Assert.True(aborted.Abort(Now.AddMinutes(2)).IsSuccess);

        UploadSession expired = Create(UploadStrategy.Proxy);
        Assert.True(expired.Expire(Now.AddHours(1)).IsSuccess);
        long expiredVersion = expired.Version;
        Assert.True(expired.Expire(Now.AddHours(2)).IsSuccess);

        Assert.Equal(committedVersion, committed.Version);
        Assert.Equal(abortedVersion, aborted.Version);
        Assert.Equal(expiredVersion, expired.Version);
        Assert.Equal(UploadReservationState.Released, aborted.Reservation.State);
        Assert.Equal(UploadReservationState.Expired, expired.Reservation.State);
    }

    [Fact]
    public void Uploads_invalid_commit_and_expired_commit_fail_explicitly()
    {
        UploadSession pending = Create(UploadStrategy.Direct);
        var invalid = pending.RequestCommit(Now.AddMinutes(1));

        UploadSession expired = Create(UploadStrategy.Direct);
        Assert.True(expired.Issue("provider-upload", Now.AddMinutes(1)).IsSuccess);
        var expiry = expired.RequestCommit(Now.AddHours(1));

        Assert.True(invalid.IsFailure);
        Assert.Equal("uploads.invalid_transition", invalid.Error?.Code);
        Assert.Equal(UploadState.Pending, pending.State);
        Assert.True(expiry.IsFailure);
        Assert.Equal("uploads.expired", expiry.Error?.Code);
        Assert.Equal(UploadState.Expired, expired.State);
    }

    [Fact]
    public void Uploads_multipart_parts_are_unique_ordered_and_contiguous_before_commit()
    {
        UploadSession upload = Create(UploadStrategy.Multipart, expectedBytes: 600);
        Assert.True(upload.Issue("multipart-id", Now.AddMinutes(1)).IsSuccess);

        Assert.True(upload.RegisterPart(
            new UploadPart(2, "etag-2", "checksum-2", 400),
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(upload.RegisterPart(
            new UploadPart(1, "etag-1", "checksum-1", 200),
            Now.AddMinutes(3)).IsSuccess);
        var duplicate = upload.RegisterPart(
            new UploadPart(1, "other", "other", 200),
            Now.AddMinutes(4));

        Assert.Equal([1, 2], upload.Parts.Select(part => part.PartNumber));
        Assert.True(duplicate.IsFailure);
        Assert.Equal("uploads.duplicate_part", duplicate.Error?.Code);
        Assert.True(upload.RequestCommit(Now.AddMinutes(5)).IsSuccess);

        UploadSession gap = Create(UploadStrategy.Multipart, expectedBytes: 400);
        Assert.True(gap.Issue("multipart-id", Now.AddMinutes(1)).IsSuccess);
        Assert.True(gap.RegisterPart(
            new UploadPart(2, "etag-2", null, 400),
            Now.AddMinutes(2)).IsSuccess);
        var gapCommit = gap.RequestCommit(Now.AddMinutes(3));

        Assert.True(gapCommit.IsFailure);
        Assert.Equal("uploads.parts_not_contiguous", gapCommit.Error?.Code);
    }

    [Fact]
    public void Uploads_accepted_session_consumes_reservation_and_uses_utc_versions()
    {
        UploadSession upload = Create(UploadStrategy.Direct);

        Assert.Equal(1, upload.Version);
        Assert.Equal(Now, upload.CreatedAtUtc);
        Assert.Equal(TimeSpan.Zero, upload.CreatedAtUtc.Offset);
        Assert.True(upload.Issue("provider-upload", Now.AddMinutes(1)).IsSuccess);
        Assert.True(upload.RequestCommit(Now.AddMinutes(2)).IsSuccess);
        Assert.True(upload.TransitionTo(UploadState.Verifying, Now.AddMinutes(3)).IsSuccess);
        Assert.True(upload.TransitionTo(UploadState.Promoting, Now.AddMinutes(4)).IsSuccess);
        Assert.True(upload.TransitionTo(UploadState.Accepted, Now.AddMinutes(5)).IsSuccess);

        Assert.Equal(UploadState.Accepted, upload.State);
        Assert.Equal(6, upload.Version);
        Assert.Equal(Now.AddMinutes(5), upload.UpdatedAtUtc);
        Assert.Equal(UploadReservationState.Consumed, upload.Reservation.State);
        Assert.True(upload.Abort(Now.AddMinutes(6)).IsFailure);
    }

    [Fact]
    public void Uploads_session_ids_must_be_uuid7()
    {
        UploadSession valid = Create(UploadStrategy.Proxy);

        Assert.NotNull(valid);
        Assert.Throws<ArgumentException>(
            () => UploadSession.Create(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                CreateIntent(UploadStrategy.Proxy, 4096),
                $"staging/01/{TenantId:N}/invalid",
                Now.AddHours(1),
                Now));
    }

    [Fact]
    public void Upload_intent_rejects_undefined_strategies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateIntent((UploadStrategy)999, 4096));
    }

    [Fact]
    public void Uploads_outcome_reconciliation_returns_to_last_successful_state()
    {
        UploadSession upload = Create(UploadStrategy.Direct);
        Assert.True(upload.Issue("provider-upload", Now.AddMinutes(1)).IsSuccess);
        Assert.True(upload.RequestCommit(Now.AddMinutes(2)).IsSuccess);
        Assert.True(upload.TransitionTo(UploadState.OutcomeUnknown, Now.AddMinutes(3)).IsSuccess);
        Assert.True(upload.TransitionTo(UploadState.Reconciling, Now.AddMinutes(4)).IsSuccess);

        var resolved = upload.ResolveReconciliation(
            providerOperationSucceeded: true,
            Now.AddMinutes(5));

        Assert.True(resolved.IsSuccess);
        Assert.Equal(UploadState.CommitRequested, upload.State);
    }

    private static UploadSession Create(
        UploadStrategy strategy,
        long expectedBytes = 4096)
    {
        UploadIntent intent = CreateIntent(strategy, expectedBytes);

        return UploadSession.Create(
            SessionId,
            intent,
            $"staging/01/{TenantId:N}/{SessionId:N}",
            Now.AddHours(1),
            Now);
    }

    private static UploadIntent CreateIntent(
        UploadStrategy strategy,
        long expectedBytes)
    {
        UploadIntegrityExpectation integrity = new(
            expectedBytes,
            new Sha256Checksum(new string('f', 64)),
            new MediaContentType("image/jpeg"));
        UploadIdempotencyMetadata idempotency = new(
            "intent-key",
            new Sha256Checksum(new string('e', 64)),
            Now.AddHours(2));
        UploadReservationMetadata reservation = UploadReservationMetadata.Create(
            ReservationId,
            expectedBytes,
            reservedObjects: 1,
            reservedComputeUnits: 0,
            Now.AddHours(1));
        UploadIntent intent = new(
            TenantId,
            ActorId,
            strategy,
            integrity,
            idempotency,
            reservation);

        return intent;
    }
}
