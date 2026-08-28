using System.Collections.ObjectModel;
using Vistara.Domain.Common;

namespace Vistara.Domain.Uploads;

public sealed class UploadSession
{
    private readonly SortedDictionary<int, UploadPart> _parts = [];
    private UploadState? _lastKnownState;

    private UploadSession(
        Guid id,
        UploadIntent intent,
        string stagingKey,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        TenantId = intent.TenantId;
        ActorId = intent.ActorId;
        Strategy = intent.Strategy;
        Integrity = intent.Integrity;
        Idempotency = intent.Idempotency;
        Reservation = intent.Reservation;
        StagingKey = stagingKey;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        State = UploadState.Pending;
        Version = 1;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid ActorId { get; }

    public UploadStrategy Strategy { get; }

    public UploadIntegrityExpectation Integrity { get; }

    public UploadIdempotencyMetadata Idempotency { get; }

    public UploadReservationMetadata Reservation { get; private set; }

    public string StagingKey { get; }

    public string? ProviderUploadId { get; private set; }

    public UploadState State { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<UploadPart> Parts =>
        new ReadOnlyCollection<UploadPart>(_parts.Values.ToArray());

    public static UploadSession Create(
        Guid id,
        UploadIntent intent,
        string stagingKey,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty || id.Version != 7)
        {
            throw new ArgumentException("Upload session ID must be UUIDv7.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(intent);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingKey);
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Expiry must follow creation.");
        }

        if (intent.Idempotency.ExpiresAtUtc < expiresAtUtc)
        {
            throw new ArgumentException(
                "Idempotency metadata must remain valid for the upload lifetime.",
                nameof(intent));
        }

        if (intent.Reservation.ExpiresAtUtc < expiresAtUtc)
        {
            throw new ArgumentException(
                "Reservation metadata must remain valid for the upload lifetime.",
                nameof(intent));
        }

        return new UploadSession(
            id,
            intent,
            stagingKey.Trim(),
            expiresAtUtc,
            createdAtUtc);
    }

    public Result Issue(string? providerUploadId, DateTimeOffset changedAtUtc)
    {
        if (State == UploadState.UploadIssued)
        {
            return Result.Success();
        }

        if (Strategy is UploadStrategy.Direct or UploadStrategy.Multipart &&
            string.IsNullOrWhiteSpace(providerUploadId))
        {
            return Result.Failure(ResultError.Validation(
                "uploads.provider_upload_id_required",
                "The selected upload strategy requires a provider upload ID."));
        }

        Result transition = TransitionTo(UploadState.UploadIssued, changedAtUtc);
        if (transition.IsSuccess)
        {
            ProviderUploadId =
                string.IsNullOrWhiteSpace(providerUploadId) ? null : providerUploadId.Trim();
        }

        return transition;
    }

    public Result RegisterPart(UploadPart part, DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(part);
        EnsureChangeTime(changedAtUtc);
        if (Strategy != UploadStrategy.Multipart)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.parts_not_supported",
                "Only multipart uploads can register parts."));
        }

        if (State != UploadState.UploadIssued)
        {
            return InvalidTransition(State, State);
        }

        if (_parts.ContainsKey(part.PartNumber))
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.duplicate_part",
                $"Part {part.PartNumber} is already registered."));
        }

        if (_parts.Values.Sum(existing => existing.SizeBytes) + part.SizeBytes >
            Integrity.ExpectedSizeBytes)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.parts_exceed_expected_size",
                "Registered parts exceed the expected upload size."));
        }

        _parts.Add(part.PartNumber, part);
        Touch(changedAtUtc);
        return Result.Success();
    }

    public Result RequestCommit(DateTimeOffset changedAtUtc)
    {
        EnsureChangeTime(changedAtUtc);
        if (State is UploadState.CommitRequested or
            UploadState.Verifying or
            UploadState.Promoting or
            UploadState.Accepted)
        {
            return Result.Success();
        }

        if (changedAtUtc >= ExpiresAtUtc)
        {
            if (UploadStateMachine.CanTransition(State, UploadState.Expired))
            {
                ApplyTransition(UploadState.Expired, changedAtUtc);
            }

            return Result.Failure(ResultError.Conflict(
                "uploads.expired",
                "The upload session has expired."));
        }

        if (State != UploadState.UploadIssued)
        {
            return InvalidTransition(State, UploadState.CommitRequested);
        }

        if (Strategy == UploadStrategy.Multipart)
        {
            Result validation = ValidateMultipartParts();
            if (validation.IsFailure)
            {
                return validation;
            }
        }

        return TransitionTo(UploadState.CommitRequested, changedAtUtc);
    }

    public Result Abort(DateTimeOffset changedAtUtc)
    {
        EnsureChangeTime(changedAtUtc);
        if (State == UploadState.Aborted)
        {
            return Result.Success();
        }

        return TransitionTo(UploadState.Aborted, changedAtUtc);
    }

    public Result Expire(DateTimeOffset changedAtUtc)
    {
        EnsureChangeTime(changedAtUtc);
        if (State == UploadState.Expired)
        {
            return Result.Success();
        }

        if (changedAtUtc < ExpiresAtUtc)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.not_expired",
                "The upload session has not reached its expiry."));
        }

        return TransitionTo(UploadState.Expired, changedAtUtc);
    }

    public Result TransitionTo(UploadState target, DateTimeOffset changedAtUtc)
    {
        EnsureChangeTime(changedAtUtc);
        if (!UploadStateMachine.CanTransition(State, target))
        {
            return InvalidTransition(State, target);
        }

        if (target == UploadState.OutcomeUnknown)
        {
            _lastKnownState = State;
        }

        ApplyTransition(target, changedAtUtc);
        return Result.Success();
    }

    public Result ResolveReconciliation(
        bool providerOperationSucceeded,
        DateTimeOffset changedAtUtc)
    {
        EnsureChangeTime(changedAtUtc);
        if (State != UploadState.Reconciling)
        {
            return InvalidTransition(State, UploadState.Reconciling);
        }

        if (!providerOperationSucceeded)
        {
            ApplyTransition(UploadState.Rejected, changedAtUtc);
            return Result.Success();
        }

        if (_lastKnownState is null)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.reconciliation_state_missing",
                "No prior successful state is available for reconciliation."));
        }

        ApplyTransition(_lastKnownState.Value, changedAtUtc);
        _lastKnownState = null;
        return Result.Success();
    }

    private Result ValidateMultipartParts()
    {
        if (_parts.Count == 0)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.parts_required",
                "A multipart upload requires at least one part."));
        }

        int expectedPartNumber = 1;
        foreach (int partNumber in _parts.Keys)
        {
            if (partNumber != expectedPartNumber)
            {
                return Result.Failure(ResultError.Conflict(
                    "uploads.parts_not_contiguous",
                    "Multipart parts must be contiguous and begin at one."));
            }

            expectedPartNumber++;
        }

        long uploadedBytes = _parts.Values.Sum(part => part.SizeBytes);
        if (uploadedBytes != Integrity.ExpectedSizeBytes)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.parts_size_mismatch",
                "Multipart part sizes do not match the expected upload size."));
        }

        return Result.Success();
    }

    private void ApplyTransition(UploadState target, DateTimeOffset changedAtUtc)
    {
        State = target;
        Reservation = target switch
        {
            UploadState.Accepted =>
                Reservation.WithState(UploadReservationState.Consumed),
            UploadState.Aborted or UploadState.Rejected =>
                Reservation.WithState(UploadReservationState.Released),
            UploadState.Expired =>
                Reservation.WithState(UploadReservationState.Expired),
            _ => Reservation,
        };
        Touch(changedAtUtc);
    }

    private void Touch(DateTimeOffset changedAtUtc)
    {
        UpdatedAtUtc = changedAtUtc;
        Version++;
    }

    private static Result InvalidTransition(UploadState current, UploadState target) =>
        Result.Failure(ResultError.Conflict(
            "uploads.invalid_transition",
            $"Upload cannot transition from {current} to {target}."));

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be UTC.", parameterName);
        }
    }

    private void EnsureChangeTime(DateTimeOffset value)
    {
        EnsureUtc(value, nameof(value));
        if (value < UpdatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Change time cannot precede the prior update.");
        }
    }
}
