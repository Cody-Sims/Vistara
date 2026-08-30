namespace Vistara.Application.Lifecycle;

[Flags]
public enum LifecycleRights
{
    None = 0,
    ListTrash = 1 << 0,
    Trash = 1 << 1,
    Restore = 1 << 2,
    Purge = 1 << 3,
    ManageHolds = 1 << 4,
    All = ListTrash | Trash | Restore | Purge | ManageHolds,
}

public enum LifecyclePrincipalKind
{
    HumanUser,
    ApiKey,
}

public enum LifecycleAuthenticationStrength
{
    PrimaryCredential,
}

public sealed record LifecycleReauthenticationContext
{
    public LifecycleReauthenticationContext(
        Guid actorId,
        DateTimeOffset verifiedAtUtc,
        LifecycleAuthenticationStrength strength)
    {
        EnsureUuid7(actorId, nameof(actorId));
        EnsureUtc(verifiedAtUtc, nameof(verifiedAtUtc));
        if (!Enum.IsDefined(strength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        ActorId = actorId;
        VerifiedAtUtc = verifiedAtUtc;
        Strength = strength;
    }

    public Guid ActorId { get; }

    public DateTimeOffset VerifiedAtUtc { get; }

    public LifecycleAuthenticationStrength Strength { get; }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle reauthentication actor identifiers must be UUIDv7 values.",
                parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lifecycle reauthentication timestamps must use UTC.",
                parameterName);
        }
    }
}

public sealed record LifecycleActorContext
{
    private LifecycleActorContext(
        Guid tenantId,
        Guid actorId,
        LifecyclePrincipalKind principalKind,
        LifecycleRights permissions,
        LifecycleReauthenticationContext? reauthentication)
    {
        TenantId = tenantId;
        ActorId = actorId;
        PrincipalKind = principalKind;
        Permissions = permissions;
        Reauthentication = reauthentication;
    }

    public Guid TenantId { get; }

    public Guid ActorId { get; }

    public LifecyclePrincipalKind PrincipalKind { get; }

    public LifecycleRights Permissions { get; }

    public LifecycleReauthenticationContext? Reauthentication { get; }

    public DateTimeOffset? AuthenticatedAtUtc =>
        Reauthentication?.VerifiedAtUtc;

    public static LifecycleActorContext Human(
        Guid tenantId,
        Guid actorId,
        LifecycleRights permissions,
        DateTimeOffset authenticatedAtUtc)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        EnsurePermissions(permissions);
        EnsureUtc(authenticatedAtUtc, nameof(authenticatedAtUtc));
        return Human(
            tenantId,
            actorId,
            permissions,
            new LifecycleReauthenticationContext(
                actorId,
                authenticatedAtUtc,
                LifecycleAuthenticationStrength.PrimaryCredential));
    }

    public static LifecycleActorContext Human(
        Guid tenantId,
        Guid actorId,
        LifecycleRights permissions,
        LifecycleReauthenticationContext? reauthentication)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        EnsurePermissions(permissions);
        if (reauthentication is not null &&
            reauthentication.ActorId != actorId)
        {
            throw new ArgumentException(
                "Lifecycle reauthentication must belong to the acting user.",
                nameof(reauthentication));
        }

        return new(
            tenantId,
            actorId,
            LifecyclePrincipalKind.HumanUser,
            permissions,
            reauthentication);
    }

    public static LifecycleActorContext ApiKey(
        Guid tenantId,
        Guid actorId,
        LifecycleRights permissions)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        EnsurePermissions(permissions);
        return new(
            tenantId,
            actorId,
            LifecyclePrincipalKind.ApiKey,
            permissions,
            reauthentication: null);
    }

    public bool HasPermission(LifecycleRights permission) =>
        permission != LifecycleRights.None &&
        (Permissions & permission) == permission;

    private static void EnsurePermissions(LifecycleRights permissions)
    {
        if ((permissions & ~LifecycleRights.All) != LifecycleRights.None)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Lifecycle actor identifiers must be UUIDv7 values.",
                parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lifecycle authentication time must be UTC.",
                parameterName);
        }
    }
}

public sealed record LifecycleAssetTarget
{
    public LifecycleAssetTarget(Guid assetId, long version)
    {
        if (assetId == Guid.Empty || assetId.Version != 7)
        {
            throw new ArgumentException(
                "The lifecycle asset identifier must be UUIDv7.",
                nameof(assetId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        AssetId = assetId;
        Version = version;
    }

    public Guid AssetId { get; }

    public long Version { get; }
}

public sealed record LifecycleAssetMutationResult(
    Guid AssetId,
    string Status,
    long Version,
    string? ErrorCode);

public sealed record LifecycleTrashListRequest
{
    public LifecycleTrashListRequest(
        int limit,
        DateTimeOffset? afterDeletedAtUtc,
        Guid? afterAssetId,
        bool descending)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (afterDeletedAtUtc is { } cursorTime &&
            cursorTime.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The trash cursor timestamp must be UTC.",
                nameof(afterDeletedAtUtc));
        }

        if (afterAssetId is { } assetId &&
            (assetId == Guid.Empty || assetId.Version != 7))
        {
            throw new ArgumentException(
                "The trash cursor asset ID must be UUIDv7.",
                nameof(afterAssetId));
        }

        if (afterDeletedAtUtc.HasValue != afterAssetId.HasValue)
        {
            throw new ArgumentException(
                "Both trash cursor values must be supplied together.");
        }

        Limit = limit;
        AfterDeletedAtUtc = afterDeletedAtUtc;
        AfterAssetId = afterAssetId;
        Descending = descending;
    }

    public int Limit { get; }

    public DateTimeOffset? AfterDeletedAtUtc { get; }

    public Guid? AfterAssetId { get; }

    public bool Descending { get; }
}

public sealed record LifecycleTrashTagSnapshot(
    Guid Id,
    string Name,
    string? Color);

public sealed record LifecycleTrashItemSnapshot(
    Guid AssetId,
    string Title,
    string? Description,
    string Status,
    string Visibility,
    long RevisionNumber,
    string ContentType,
    string Format,
    int Width,
    int Height,
    long SizeBytes,
    DateTimeOffset? CapturedAtUtc,
    DateTimeOffset ImportedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool Favorite,
    IReadOnlyList<LifecycleTrashTagSnapshot> Tags,
    long Version,
    DateTimeOffset DeletedAtUtc,
    DateTimeOffset PurgeAtUtc,
    string Reason,
    int ActiveHoldCount,
    int BlockingReferenceCount,
    long EstimatedReclaimBytes);

public sealed record LifecycleTrashPage(
    IReadOnlyList<LifecycleTrashItemSnapshot> Items,
    bool HasMore);

public sealed record LifecycleTrashQuery(
    Guid TenantId,
    Guid ActorId,
    LifecycleTrashListRequest Request,
    DateTimeOffset EvaluatedAtUtc);

public sealed record LifecycleTrashCommand(
    Guid TenantId,
    Guid ActorId,
    IReadOnlyList<LifecycleAssetTarget> Targets,
    string Reason,
    DateTimeOffset DeletedAtUtc,
    DateTimeOffset PurgeAtUtc);

public sealed record LifecyclePurgeCandidateSnapshot(
    Guid AssetId,
    long RevisionNumber,
    string Title,
    bool Eligible,
    IReadOnlyList<string> Barriers,
    int SharedLinkImpact,
    long EstimatedReclaimBytes);

public sealed record LifecyclePurgeDryRunSnapshot(
    Guid BatchId,
    string State,
    string DryRunDigest,
    DateTimeOffset ExpiresAtUtc,
    int CandidateCount,
    int EligibleCount,
    long EstimatedReclaimBytes,
    IReadOnlyList<LifecyclePurgeCandidateSnapshot> Items,
    long Version,
    bool Replayed);

public sealed record LifecyclePurgeItemSnapshot(
    Guid AssetId,
    long RevisionNumber,
    string Result,
    long ReclaimedBytes,
    string? ErrorCode);

public sealed record LifecyclePurgeBatchSnapshot(
    Guid BatchId,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ApprovedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int CandidateCount,
    int EligibleCount,
    int ProcessedCount,
    long ReclaimedBytes,
    IReadOnlyList<LifecyclePurgeItemSnapshot> Items,
    long Version,
    bool Replayed);

public sealed record LifecycleConfirmPurgeCommand(
    Guid TenantId,
    Guid ActorId,
    Guid BatchId,
    long ExpectedVersion,
    string DryRunDigest,
    string IdempotencyKey,
    Guid JobId,
    DateTimeOffset ConfirmedAtUtc);

public sealed record LifecycleJobSubmission(
    Guid JobId,
    string State,
    int SubmittedCount,
    DateTimeOffset SubmittedAtUtc,
    bool Replayed);

public sealed record LifecycleRestoreCommand(
    Guid TenantId,
    Guid ActorId,
    IReadOnlyList<LifecycleAssetTarget> Targets,
    string IdempotencyKey,
    Guid JobId,
    DateTimeOffset SubmittedAtUtc);

public sealed record LifecycleCreatePurgeDryRunCommand(
    Guid TenantId,
    Guid ActorId,
    IReadOnlyList<LifecycleAssetTarget> Targets,
    string IdempotencyKey,
    Guid BatchId,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record LifecycleHoldSnapshot(
    Guid HoldId,
    Guid AssetId,
    string Reason,
    DateTimeOffset CreatedAtUtc,
    bool Active,
    long Version);

public sealed record LifecyclePlaceHoldCommand(
    Guid TenantId,
    Guid ActorId,
    Guid AssetId,
    Guid HoldId,
    string Reason,
    DateTimeOffset CreatedAtUtc);

public sealed record LifecycleReleaseHoldCommand(
    Guid TenantId,
    Guid ActorId,
    Guid HoldId,
    DateTimeOffset ReleasedAtUtc);
