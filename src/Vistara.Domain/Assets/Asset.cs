using System.Collections.ObjectModel;
using Vistara.Domain.Common;

namespace Vistara.Domain.Assets;

public sealed class Asset
{
    private readonly List<AssetRevision> _revisions = [];

    private Asset(
        Guid id,
        Guid tenantId,
        Guid ownerId,
        string title,
        AssetVisibility visibility,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        OwnerId = ownerId;
        Title = title;
        Visibility = visibility;
        Status = AssetStatus.Processing;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Guid OwnerId { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public AssetStatus Status { get; private set; }

    public AssetVisibility Visibility { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public IReadOnlyList<AssetRevision> Revisions =>
        new ReadOnlyCollection<AssetRevision>(_revisions.ToArray());

    public AssetRevision? CurrentRevision =>
        _revisions.Count == 0 ? null : _revisions[^1];

    public static Asset Create(
        Guid id,
        Guid tenantId,
        Guid ownerId,
        string title,
        AssetVisibility visibility,
        DateTimeOffset createdAtUtc)
    {
        EnsureId(id, nameof(id));
        EnsureId(tenantId, nameof(tenantId));
        EnsureId(ownerId, nameof(ownerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        EnsureVisibility(visibility);
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        return new Asset(
            id,
            tenantId,
            ownerId,
            title.Trim(),
            visibility,
            createdAtUtc);
    }

    public Result AddRevision(AssetRevision revision, DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(revision);
        EnsureChangeTime(changedAtUtc);
        if (revision.TenantId != TenantId)
        {
            return Result.Failure(ResultError.Conflict(
                "assets.tenant_mismatch",
                "The revision belongs to another tenant."));
        }

        if (revision.AssetId != Id)
        {
            return Result.Failure(ResultError.Conflict(
                "assets.asset_mismatch",
                "The revision belongs to another asset."));
        }

        long expectedRevision = (CurrentRevision?.RevisionNumber ?? 0) + 1;
        if (revision.RevisionNumber != expectedRevision)
        {
            return Result.Failure(ResultError.Conflict(
                "assets.revision_out_of_sequence",
                $"Expected revision {expectedRevision}."));
        }

        _revisions.Add(revision);
        UpdatedAtUtc = changedAtUtc;
        Version++;
        return Result.Success();
    }

    public Result UpdateMetadata(
        string title,
        string? description,
        AssetVisibility visibility,
        long expectedVersion,
        DateTimeOffset changedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        EnsureChangeTime(changedAtUtc);
        EnsureVisibility(visibility);
        if (expectedVersion != Version)
        {
            return Result.Failure(ResultError.Conflict(
                "assets.version_conflict",
                $"Expected asset version {Version}."));
        }

        string normalizedTitle = title.Trim();
        string? normalizedDescription =
            string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (Title == normalizedTitle &&
            Description == normalizedDescription &&
            Visibility == visibility)
        {
            return Result.Success();
        }

        Title = normalizedTitle;
        Description = normalizedDescription;
        Visibility = visibility;
        UpdatedAtUtc = changedAtUtc;
        Version++;
        return Result.Success();
    }

    private static void EnsureId(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("IDs must be non-empty UUIDv7 values.", parameterName);
        }
    }

    private static void EnsureVisibility(AssetVisibility visibility)
    {
        if (!Enum.IsDefined(visibility))
        {
            throw new ArgumentOutOfRangeException(
                nameof(visibility),
                "The asset visibility is invalid.");
        }
    }

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
