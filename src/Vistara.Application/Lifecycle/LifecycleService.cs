using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Application.Lifecycle;

public sealed class LifecycleService(
    ILifecycleStore store,
    IClock clock,
    IUuid7Generator idGenerator)
{
    public static readonly TimeSpan DefaultRecoveryWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan PurgeReauthenticationWindow =
        TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PurgeDryRunLifetime =
        TimeSpan.FromMinutes(15);

    private readonly ILifecycleStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));

    public ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
        LifecycleActorContext actor,
        LifecycleTrashListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.ListTrash))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecycleTrashPage>(
                    LifecycleApplicationErrors.Forbidden));
        }

        return _store.ListTrashAsync(
            new LifecycleTrashQuery(
                actor.TenantId,
                actor.ActorId,
                request,
                RequireUtc(_clock.UtcNow)),
            cancellationToken);
    }

    public ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
        LifecycleActorContext actor,
        IReadOnlyList<LifecycleAssetTarget> targets,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateTargets(targets);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.Trash))
        {
            return ValueTask.FromResult(
                Result.Failure<IReadOnlyList<LifecycleAssetMutationResult>>(
                    LifecycleApplicationErrors.Forbidden));
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        _ = _idGenerator;
        return _store.TrashAsync(
            new LifecycleTrashCommand(
                actor.TenantId,
                actor.ActorId,
                targets.ToArray(),
                reason.Trim(),
                now,
                now.Add(DefaultRecoveryWindow)),
            cancellationToken);
    }

    public ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
        LifecycleActorContext actor,
        Guid batchId,
        long expectedVersion,
        string dryRunDigest,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUuid7(batchId, nameof(batchId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedVersion);
        ValidateDigest(dryRunDigest);
        ValidateIdempotencyKey(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.Purge))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.Forbidden));
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        LifecycleReauthenticationContext? reauthentication =
            actor.Reauthentication;
        if (actor.PrincipalKind != LifecyclePrincipalKind.HumanUser ||
            reauthentication is null ||
            reauthentication.ActorId != actor.ActorId ||
            reauthentication.Strength !=
                LifecycleAuthenticationStrength.PrimaryCredential ||
            reauthentication.VerifiedAtUtc > now ||
            now - reauthentication.VerifiedAtUtc > PurgeReauthenticationWindow)
        {
            return ValueTask.FromResult(
                Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.ReauthenticationRequired));
        }

        Guid jobId = _idGenerator.NewId();
        EnsureUuid7(jobId, nameof(_idGenerator));
        return _store.ConfirmPurgeAsync(
            new LifecycleConfirmPurgeCommand(
                actor.TenantId,
                actor.ActorId,
                batchId,
                expectedVersion,
                dryRunDigest.ToLowerInvariant(),
                idempotencyKey,
                jobId,
                now),
            cancellationToken);
    }

    public ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
        LifecycleActorContext actor,
        IReadOnlyList<LifecycleAssetTarget> targets,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateTargets(targets);
        ValidateIdempotencyKey(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.Restore))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecycleJobSubmission>(
                    LifecycleApplicationErrors.Forbidden));
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        Guid jobId = _idGenerator.NewId();
        EnsureUuid7(jobId, nameof(_idGenerator));
        return _store.SubmitRestoreAsync(
            new LifecycleRestoreCommand(
                actor.TenantId,
                actor.ActorId,
                targets.ToArray(),
                idempotencyKey,
                jobId,
                now),
            cancellationToken);
    }

    public ValueTask<Result<LifecyclePurgeDryRunSnapshot>> CreatePurgeDryRunAsync(
        LifecycleActorContext actor,
        IReadOnlyList<LifecycleAssetTarget> targets,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ValidateTargets(targets);
        ValidateIdempotencyKey(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.Purge))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecyclePurgeDryRunSnapshot>(
                    LifecycleApplicationErrors.Forbidden));
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        Guid batchId = _idGenerator.NewId();
        EnsureUuid7(batchId, nameof(_idGenerator));
        return _store.CreatePurgeDryRunAsync(
            new LifecycleCreatePurgeDryRunCommand(
                actor.TenantId,
                actor.ActorId,
                targets.ToArray(),
                idempotencyKey,
                batchId,
                now,
                now.Add(PurgeDryRunLifetime)),
            cancellationToken);
    }

    public ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
        LifecycleActorContext actor,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUuid7(batchId, nameof(batchId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.Purge))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecyclePurgeBatchSnapshot>(
                    LifecycleApplicationErrors.Forbidden));
        }

        return _store.GetPurgeBatchAsync(
            actor.TenantId,
            actor.ActorId,
            batchId,
            cancellationToken);
    }

    public ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
        LifecycleActorContext actor,
        Guid assetId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUuid7(assetId, nameof(assetId));
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.ManageHolds))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecycleHoldSnapshot>(
                    LifecycleApplicationErrors.Forbidden));
        }

        DateTimeOffset now = RequireUtc(_clock.UtcNow);
        Guid holdId = _idGenerator.NewId();
        EnsureUuid7(holdId, nameof(_idGenerator));
        return _store.PlaceHoldAsync(
            new LifecyclePlaceHoldCommand(
                actor.TenantId,
                actor.ActorId,
                assetId,
                holdId,
                reason.Trim(),
                now),
            cancellationToken);
    }

    public ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
        LifecycleActorContext actor,
        Guid holdId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnsureUuid7(holdId, nameof(holdId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!actor.HasPermission(LifecycleRights.ManageHolds))
        {
            return ValueTask.FromResult(
                Result.Failure<LifecycleHoldSnapshot>(
                    LifecycleApplicationErrors.Forbidden));
        }

        return _store.ReleaseHoldAsync(
            new LifecycleReleaseHoldCommand(
                actor.TenantId,
                actor.ActorId,
                holdId,
                RequireUtc(_clock.UtcNow)),
            cancellationToken);
    }

    private static void ValidateTargets(IReadOnlyList<LifecycleAssetTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targets),
                "Lifecycle operations require between 1 and 200 assets.");
        }

        if (targets.Select(target => target.AssetId).Distinct().Count() != targets.Count)
        {
            throw new ArgumentException(
                "Lifecycle asset identifiers must be unique.",
                nameof(targets));
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The lifecycle clock must return UTC.");
        }

        return value;
    }

    private static void ValidateDigest(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "A purge digest must be a SHA-256 hexadecimal value.",
                nameof(value));
        }
    }

    private static void ValidateIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.' or ':')))
        {
            throw new ArgumentException(
                "The lifecycle idempotency key is invalid.",
                nameof(value));
        }
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle identifiers must be UUIDv7 values.",
                parameterName);
        }
    }
}

public static class LifecycleApplicationErrors
{
    public static readonly ResultError Forbidden = ResultError.Forbidden(
        "lifecycle.forbidden",
        "The actor is not permitted to perform this lifecycle operation.");

    public static readonly ResultError ReauthenticationRequired =
        ResultError.Forbidden(
            "lifecycle.reauthentication_required",
            "Recent human reauthentication is required for permanent deletion.");

    public static readonly ResultError NotFound = ResultError.NotFound(
        "lifecycle.not_found",
        "The lifecycle resource was not found.");

    public static readonly ResultError VersionConflict = ResultError.Conflict(
        "lifecycle.version_conflict",
        "The lifecycle resource version changed.");

    public static readonly ResultError InvalidState = ResultError.Conflict(
        "lifecycle.invalid_state",
        "The lifecycle operation is not valid in the current state.");

    public static readonly ResultError IdempotencyConflict = ResultError.Conflict(
        "lifecycle.idempotency_conflict",
        "The idempotency key was already used for a different request.");

    public static readonly ResultError Unavailable = ResultError.Unavailable(
        "lifecycle.unavailable",
        "The lifecycle service is unavailable.");

    public static readonly ResultError SeparateApproverRequired =
        ResultError.Forbidden(
            "lifecycle.separate_approver_required",
            "A different recently authenticated human must approve permanent deletion.");

    public static readonly ResultError DryRunStale = ResultError.Conflict(
        "lifecycle.dry_run_stale",
        "The purge candidates changed after the dry run.");

    public static readonly ResultError DryRunExpired = ResultError.Conflict(
        "lifecycle.dry_run_expired",
        "The purge dry run expired.");

    public static readonly ResultError PurgeBlocked = ResultError.Conflict(
        "lifecycle.purge_blocked",
        "The purge is blocked by retention, holds, revisions, or references.");
}
