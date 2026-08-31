using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Gallery.Queries;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Gallery.Queries;

public sealed class RelationalAssetQueryStore(
    VistaraDbContext context) : IAssetQueryStore
{
    private const int MaximumFacetValues = 100;
    private static readonly string[] SafeMetadataKeys =
    [
        "orientation",
        "cameraMake",
        "cameraModel",
        "lensModel",
        "colorSpace",
    ];
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context =
        context ?? throw new ArgumentNullException(nameof(context));

    public async ValueTask<AssetQuerySlice> QueryAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        AssetQueryWindow window,
        CancellationToken cancellationToken)
    {
        EnsureTenant(scope);
        cancellationToken.ThrowIfCancellationRequested();
        IQueryable<AssetProjection> query = ApplyCriteria(
            BaseQuery(scope.ActorId, window.SnapshotAtUtc),
            scope,
            criteria);
        AssetProjection[] rows = await ApplyKeysetAndOrder(
                query,
                criteria,
                window.Continuation)
            .Take(criteria.Limit + 1)
            .ToArrayAsync(cancellationToken);
        bool hasMore = rows.Length > criteria.Limit;
        AssetProjection[] pageRows = rows.Take(criteria.Limit).ToArray();
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetTag>> tags =
            await LoadTagsAsync(pageRows, cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetDeliverySource>> renditions =
            await LoadRenditionsAsync(pageRows, cancellationToken);
        AssetQueryItem[] items = pageRows
            .Select(row => ToItem(row, tags, renditions))
            .ToArray();
        AssetQueryKey? nextKey = hasMore && pageRows.Length > 0
            ? ToKey(pageRows[^1], criteria.Sort)
            : null;
        return new AssetQuerySlice(items, nextKey, hasMore);
    }

    public async ValueTask<AssetDetail?> GetAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(scope);
        AssetProjection? row = await BaseQuery(scope.ActorId, DateTimeOffset.MaxValue)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == assetId,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<AssetTag>> tags =
            await LoadTagsAsync([row], cancellationToken);
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetDeliverySource>> renditions =
            await LoadRenditionsAsync([row], cancellationToken);
        AssetMetadata? metadata =
            await GetMetadataAsync(scope, assetId, cancellationToken);
        AssetAlbum[] albums = await (
            from item in _context.AlbumItems.AsNoTracking()
            join album in _context.Albums.AsNoTracking()
                on item.AlbumId equals album.Id
            where item.AssetId == assetId
            orderby album.Name, album.Id
            select new AssetAlbum(album.Id, album.Name))
            .Take(200)
            .ToArrayAsync(cancellationToken);
        return new AssetDetail(
            ToItem(row, tags, renditions),
            metadata!,
            albums);
    }

    public async ValueTask<AssetMetadata?> GetMetadataAsync(
        AssetQueryScope scope,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(scope);
        MetadataProjection? row = await (
            from asset in _context.Assets.AsNoTracking()
            join revision in _context.AssetRevisions.AsNoTracking()
                on asset.CurrentRevisionId equals revision.Id
            where asset.Id == assetId &&
                asset.Status != "Trashed" &&
                asset.Status != "Purged"
            select new MetadataProjection(
                asset.Id,
                revision.RevisionNumber,
                asset.CapturedAtUtc,
                revision.SafeMetadataJson,
                revision.PrivateMetadataJson))
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToMetadata(row);
    }

    public async ValueTask<IReadOnlyList<AssetFacetGroup>> GetFacetsAsync(
        AssetQueryScope scope,
        AssetQueryCriteria criteria,
        DateTimeOffset snapshotAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureTenant(scope);
        IQueryable<AssetProjection> query = ApplyCriteria(
            BaseQuery(scope.ActorId, snapshotAtUtc),
            scope,
            criteria);
        var statusRows = await query
            .GroupBy(row => row.Status)
            .Select(group => new
            {
                Value = group.Key,
                Count = group.LongCount(),
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.Value)
            .Take(MaximumFacetValues + 1)
            .ToArrayAsync(cancellationToken);
        AssetFacetValue[] statuses = statusRows
            .Select(row => new AssetFacetValue(row.Value, row.Value, row.Count))
            .ToArray();
        var contentTypeRows = await query
            .GroupBy(row => row.ContentType)
            .Select(group => new
            {
                Value = group.Key,
                Count = group.LongCount(),
            })
            .OrderByDescending(value => value.Count)
            .ThenBy(value => value.Value)
            .Take(MaximumFacetValues + 1)
            .ToArrayAsync(cancellationToken);
        AssetFacetValue[] contentTypes = contentTypeRows
            .Select(row => new AssetFacetValue(row.Value, row.Value, row.Count))
            .ToArray();
        IQueryable<Guid> matchingAssetIds = query.Select(row => row.Id);
        var tagRows = await (
            from assetTag in _context.AssetTags.AsNoTracking()
            join tag in _context.Tags.AsNoTracking()
                on assetTag.TagId equals tag.Id
            where matchingAssetIds.Contains(assetTag.AssetId)
            group tag by new { tag.Id, tag.DisplayName } into grouped
            orderby grouped.LongCount() descending, grouped.Key.DisplayName
            select new
            {
                grouped.Key.Id,
                grouped.Key.DisplayName,
                Count = grouped.LongCount(),
            })
            .Take(MaximumFacetValues + 1)
            .ToArrayAsync(cancellationToken);
        AssetFacetValue[] tags = tagRows
            .Select(row => new AssetFacetValue(
                row.Id.ToString(),
                row.DisplayName,
                row.Count))
            .ToArray();
        return
        [
            ToFacetGroup("status", statuses),
            ToFacetGroup("contentType", contentTypes),
            ToFacetGroup("tag", tags),
        ];
    }

    public async ValueTask<AssetUpdateStoreResult> UpdateAsync(
        AssetQueryScope scope,
        Guid assetId,
        long expectedVersion,
        string idempotencyKey,
        AssetMetadataPatch patch,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        EnsureTenant(scope);
        if (!ValidatePatch(patch) ||
            expectedVersion < 1 ||
            string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128)
        {
            return AssetUpdateStoreResult.ValidationFailed();
        }

        string source = IdempotencySource(idempotencyKey);
        string fingerprint = PatchFingerprint(expectedVersion, patch);
        AssetMetadataHistoryRow? replay = await _context.AssetMetadataHistory
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.AssetId == assetId && row.Source == source,
                cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(
                    ReadFingerprint(replay.ChangesJson),
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return AssetUpdateStoreResult.ValidationFailed();
            }

            AssetDetail? replayed =
                await GetAsync(scope, assetId, cancellationToken);
            return replayed is null
                ? AssetUpdateStoreResult.NotFound()
                : AssetUpdateStoreResult.Replayed(replayed);
        }

        AssetRow? asset = await _context.Assets.SingleOrDefaultAsync(
            row =>
                row.Id == assetId &&
                row.Status != "Trashed" &&
                row.Status != "Purged",
            cancellationToken);
        if (asset is null)
        {
            return AssetUpdateStoreResult.NotFound();
        }

        if (asset.Version != expectedVersion)
        {
            return AssetUpdateStoreResult.VersionConflict();
        }

        ApplyPatch(asset, patch);
        asset.Version++;
        asset.UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
        _context.AssetMetadataHistory.Add(new AssetMetadataHistoryRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = scope.TenantId,
            AssetId = assetId,
            ActorUserId = scope.ActorId,
            Source = source,
            ChangesJson = JsonSerializer.Serialize(
                new UpdateAudit(fingerprint, ChangedFields(patch)),
                JsonOptions),
            ChangedAtUtc = updatedAtUtc.ToUniversalTime(),
        });
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            _context.ChangeTracker.Clear();
            AssetMetadataHistoryRow? concurrentReplay =
                await _context.AssetMetadataHistory
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        row => row.AssetId == assetId && row.Source == source,
                        cancellationToken);
            if (concurrentReplay is not null &&
                string.Equals(
                    ReadFingerprint(concurrentReplay.ChangesJson),
                    fingerprint,
                    StringComparison.Ordinal))
            {
                AssetDetail? replayed =
                    await GetAsync(scope, assetId, cancellationToken);
                return replayed is null
                    ? AssetUpdateStoreResult.NotFound()
                    : AssetUpdateStoreResult.Replayed(replayed);
            }

            return AssetUpdateStoreResult.VersionConflict();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            AssetMetadataHistoryRow? concurrentReplay =
                await _context.AssetMetadataHistory
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        row => row.AssetId == assetId && row.Source == source,
                        cancellationToken);
            if (concurrentReplay is not null &&
                string.Equals(
                    ReadFingerprint(concurrentReplay.ChangesJson),
                    fingerprint,
                    StringComparison.Ordinal))
            {
                AssetDetail? replayed =
                    await GetAsync(scope, assetId, cancellationToken);
                return replayed is null
                    ? AssetUpdateStoreResult.NotFound()
                    : AssetUpdateStoreResult.Replayed(replayed);
            }

            throw;
        }

        AssetDetail? detail = await GetAsync(scope, assetId, cancellationToken);
        return detail is null
            ? AssetUpdateStoreResult.NotFound()
            : AssetUpdateStoreResult.Updated(detail);
    }

    private IQueryable<AssetProjection> BaseQuery(
        Guid actorId,
        DateTimeOffset snapshotAtUtc) =>
        from asset in _context.Assets.AsNoTracking()
        join revision in _context.AssetRevisions.AsNoTracking()
            on asset.CurrentRevisionId equals revision.Id
        join blob in _context.Blobs.AsNoTracking()
            on revision.BlobId equals blob.Id
        where asset.Status != "Trashed" &&
            asset.Status != "Purged" &&
            asset.CreatedAtUtc <= snapshotAtUtc &&
            asset.UpdatedAtUtc <= snapshotAtUtc &&
            blob.State == "Active"
        select new AssetProjection
        {
            Id = asset.Id,
            Title = asset.Title,
            Description = asset.Description,
            Status = asset.Status,
            Visibility = asset.Visibility,
            RevisionNumber = revision.RevisionNumber,
            RevisionId = revision.Id,
            ContentType = revision.DetectedContentType,
            Format = revision.DetectedFormat,
            Width = revision.Width,
            Height = revision.Height,
            SizeBytes = blob.SizeBytes,
            CapturedAt = asset.CapturedAtUtc,
            ImportedAt = asset.CreatedAtUtc,
            UpdatedAt = asset.UpdatedAtUtc,
            Favorite = _context.AssetFavorites.Any(favorite =>
                favorite.AssetId == asset.Id && favorite.UserId == actorId),
            Version = asset.Version,
        };

    private IQueryable<AssetProjection> ApplyCriteria(
        IQueryable<AssetProjection> query,
        AssetQueryScope scope,
        AssetQueryCriteria criteria)
    {
        if (criteria.Search is not null)
        {
            string search = criteria.Search;
            query = query.Where(row =>
                row.Title.Contains(search) ||
                (row.Description != null && row.Description.Contains(search)) ||
                _context.AssetTags.Any(assetTag =>
                    assetTag.AssetId == row.Id &&
                    _context.Tags.Any(tag =>
                        tag.Id == assetTag.TagId &&
                        tag.DisplayName.Contains(search))));
        }

        if (criteria.Statuses.Count > 0)
        {
            query = query.Where(row => criteria.Statuses.Contains(row.Status));
        }

        if (criteria.ContentTypes.Count > 0)
        {
            query = query.Where(row =>
                criteria.ContentTypes.Contains(row.ContentType));
        }

        if (criteria.AlbumId is Guid albumId)
        {
            query = query.Where(row => _context.AlbumItems.Any(item =>
                item.AlbumId == albumId && item.AssetId == row.Id));
        }

        foreach (Guid tagId in criteria.TagIds)
        {
            Guid capturedTagId = tagId;
            query = query.Where(row => _context.AssetTags.Any(assetTag =>
                assetTag.AssetId == row.Id && assetTag.TagId == capturedTagId));
        }

        if (criteria.Favorite is bool favorite)
        {
            query = query.Where(row => row.Favorite == favorite);
        }

        if (criteria.CapturedFrom is DateTimeOffset capturedFrom)
        {
            query = query.Where(row =>
                row.CapturedAt != null && row.CapturedAt >= capturedFrom);
        }

        if (criteria.CapturedTo is DateTimeOffset capturedTo)
        {
            query = query.Where(row =>
                row.CapturedAt != null && row.CapturedAt <= capturedTo);
        }

        if (criteria.ImportedFrom is DateTimeOffset importedFrom)
        {
            query = query.Where(row => row.ImportedAt >= importedFrom);
        }

        if (criteria.ImportedTo is DateTimeOffset importedTo)
        {
            query = query.Where(row => row.ImportedAt <= importedTo);
        }

        return query;
    }

    private static IQueryable<AssetProjection> ApplyKeysetAndOrder(
        IQueryable<AssetProjection> query,
        AssetQueryCriteria criteria,
        AssetQueryKey? continuation) =>
        criteria.Sort switch
        {
            AssetSort.CapturedAt => OrderCaptured(
                query,
                criteria.Direction,
                continuation),
            AssetSort.ImportedAt => OrderInstant(
                query,
                criteria.Direction,
                continuation,
                AssetSort.ImportedAt),
            AssetSort.UpdatedAt => OrderInstant(
                query,
                criteria.Direction,
                continuation,
                AssetSort.UpdatedAt),
            AssetSort.Title => OrderTitle(
                query,
                criteria.Direction,
                continuation),
            AssetSort.SizeBytes => OrderSize(
                query,
                criteria.Direction,
                continuation),
            _ => throw new ArgumentOutOfRangeException(nameof(criteria)),
        };

    private static IQueryable<AssetProjection> OrderCaptured(
        IQueryable<AssetProjection> query,
        SortDirection direction,
        AssetQueryKey? continuation)
    {
        if (continuation is not null)
        {
            DateTimeOffset instant = continuation.InstantValue ??
                throw new InvalidOperationException("The captured cursor is invalid.");
            if (direction == SortDirection.Descending)
            {
                query = query.Where(row =>
                    (row.CapturedAt == null ? 1 : 0) > continuation.NullRank ||
                    ((row.CapturedAt == null ? 1 : 0) == continuation.NullRank &&
                        ((row.CapturedAt ?? row.ImportedAt) < instant ||
                        ((row.CapturedAt ?? row.ImportedAt) == instant &&
                            row.Id.CompareTo(continuation.AssetId) < 0))));
            }
            else
            {
                query = query.Where(row =>
                    (row.CapturedAt == null ? 1 : 0) > continuation.NullRank ||
                    ((row.CapturedAt == null ? 1 : 0) == continuation.NullRank &&
                        ((row.CapturedAt ?? row.ImportedAt) > instant ||
                        ((row.CapturedAt ?? row.ImportedAt) == instant &&
                            row.Id.CompareTo(continuation.AssetId) > 0))));
            }
        }

        IOrderedQueryable<AssetProjection> ordered = query
            .OrderBy(row => row.CapturedAt == null ? 1 : 0);
        return direction == SortDirection.Descending
            ? ordered.ThenByDescending(row => row.CapturedAt ?? row.ImportedAt)
                .ThenByDescending(row => row.Id)
            : ordered.ThenBy(row => row.CapturedAt ?? row.ImportedAt)
                .ThenBy(row => row.Id);
    }

    private static IQueryable<AssetProjection> OrderInstant(
        IQueryable<AssetProjection> query,
        SortDirection direction,
        AssetQueryKey? continuation,
        AssetSort sort)
    {
        if (continuation?.InstantValue is DateTimeOffset instant)
        {
            query = direction == SortDirection.Descending
                ? query.Where(row =>
                    (sort == AssetSort.ImportedAt
                        ? row.ImportedAt
                        : row.UpdatedAt) < instant ||
                    ((sort == AssetSort.ImportedAt
                        ? row.ImportedAt
                        : row.UpdatedAt) == instant &&
                        row.Id.CompareTo(continuation.AssetId) < 0))
                : query.Where(row =>
                    (sort == AssetSort.ImportedAt
                        ? row.ImportedAt
                        : row.UpdatedAt) > instant ||
                    ((sort == AssetSort.ImportedAt
                        ? row.ImportedAt
                        : row.UpdatedAt) == instant &&
                        row.Id.CompareTo(continuation.AssetId) > 0));
        }

        return sort == AssetSort.ImportedAt
            ? direction == SortDirection.Descending
                ? query.OrderByDescending(row => row.ImportedAt)
                    .ThenByDescending(row => row.Id)
                : query.OrderBy(row => row.ImportedAt).ThenBy(row => row.Id)
            : direction == SortDirection.Descending
                ? query.OrderByDescending(row => row.UpdatedAt)
                    .ThenByDescending(row => row.Id)
                : query.OrderBy(row => row.UpdatedAt).ThenBy(row => row.Id);
    }

#pragma warning disable CA1309
    private static IQueryable<AssetProjection> OrderTitle(
        IQueryable<AssetProjection> query,
        SortDirection direction,
        AssetQueryKey? continuation)
    {
        if (continuation?.TextValue is string title)
        {
            query = direction == SortDirection.Descending
                ? query.Where(row =>
                    string.Compare(row.Title, title) < 0 ||
                    (row.Title == title &&
                        row.Id.CompareTo(continuation.AssetId) < 0))
                : query.Where(row =>
                    string.Compare(row.Title, title) > 0 ||
                    (row.Title == title &&
                        row.Id.CompareTo(continuation.AssetId) > 0));
        }

        return direction == SortDirection.Descending
            ? query.OrderByDescending(row => row.Title)
                .ThenByDescending(row => row.Id)
            : query.OrderBy(row => row.Title).ThenBy(row => row.Id);
    }
#pragma warning restore CA1309

    private static IQueryable<AssetProjection> OrderSize(
        IQueryable<AssetProjection> query,
        SortDirection direction,
        AssetQueryKey? continuation)
    {
        if (continuation?.NumberValue is long size)
        {
            query = direction == SortDirection.Descending
                ? query.Where(row =>
                    row.SizeBytes < size ||
                    (row.SizeBytes == size &&
                        row.Id.CompareTo(continuation.AssetId) < 0))
                : query.Where(row =>
                    row.SizeBytes > size ||
                    (row.SizeBytes == size &&
                        row.Id.CompareTo(continuation.AssetId) > 0));
        }

        return direction == SortDirection.Descending
            ? query.OrderByDescending(row => row.SizeBytes)
                .ThenByDescending(row => row.Id)
            : query.OrderBy(row => row.SizeBytes).ThenBy(row => row.Id);
    }

    private async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<AssetTag>>>
        LoadTagsAsync(
            IReadOnlyList<AssetProjection> rows,
            CancellationToken cancellationToken)
    {
        Guid[] ids = rows.Select(row => row.Id).ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AssetTag>>();
        }

        AssetTagProjection[] tags = await (
            from assetTag in _context.AssetTags.AsNoTracking()
            join tag in _context.Tags.AsNoTracking()
                on assetTag.TagId equals tag.Id
            where ids.Contains(assetTag.AssetId)
            orderby tag.DisplayName, tag.Id
            select new AssetTagProjection(
                assetTag.AssetId,
                tag.Id,
                tag.DisplayName,
                tag.Color))
            .Take(ids.Length * MaximumFacetValues)
            .ToArrayAsync(cancellationToken);
        return tags
            .GroupBy(tag => tag.AssetId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AssetTag>)group
                    .Select(tag => new AssetTag(tag.TagId, tag.Name, tag.Color))
                    .ToArray());
    }

    private async ValueTask<
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetDeliverySource>>>
        LoadRenditionsAsync(
            IReadOnlyList<AssetProjection> rows,
            CancellationToken cancellationToken)
    {
        Guid[] ids = rows.Select(row => row.Id).ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<AssetDeliverySource>>();
        }

        DeliveryProjection[] deliveries = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row => ids.Contains(row.AssetId) && row.State == "Ready")
            .OrderBy(row => row.AssetId)
            .ThenBy(row => row.Width * row.Height)
            .Select(row => new DeliveryProjection(
                row.AssetId,
                row.Id,
                row.PresetName,
                row.Width,
                row.Height,
                row.RepresentationContentType!,
                row.IsPublic,
                row.PipelineId,
                row.SourceSha256,
                row.RecipeSha256,
                row.Extension))
            .Take(ids.Length * 20)
            .ToArrayAsync(cancellationToken);
        return deliveries
            .GroupBy(delivery => delivery.AssetId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AssetDeliverySource>)group
                    .Select(ToDeliverySource)
                    .ToArray());
    }

    private static AssetDeliverySource ToDeliverySource(DeliveryProjection row)
    {
        string path = AssetRenditionDelivery.Path(
            row.AssetId,
            row.RequestId,
            row.IsPublic,
            row.PipelineId,
            row.SourceSha256,
            row.RecipeSha256,
            row.Extension);
        return new AssetDeliverySource(
            row.Kind,
            path,
            row.Width,
            row.Height,
            row.ContentType);
    }

    private static AssetQueryItem ToItem(
        AssetProjection row,
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetTag>> tags,
        IReadOnlyDictionary<Guid, IReadOnlyList<AssetDeliverySource>> renditions) =>
        new(
            row.Id,
            row.Title,
            row.Description,
            row.Status,
            row.Visibility,
            row.RevisionNumber,
            row.ContentType,
            row.Format,
            row.Width,
            row.Height,
            row.SizeBytes,
            row.CapturedAt,
            row.ImportedAt,
            row.UpdatedAt,
            row.Favorite,
            tags.GetValueOrDefault(row.Id) ?? [],
            renditions.GetValueOrDefault(row.Id) ??
            [
                new AssetDeliverySource(
                    "original",
                    $"/api/v1/assets/{row.Id:D}/original",
                    row.Width,
                    row.Height,
                    row.ContentType),
            ],
            row.Version);

    private static AssetQueryKey ToKey(
        AssetProjection row,
        AssetSort sort) =>
        sort switch
        {
            AssetSort.CapturedAt => new AssetQueryKey(
                row.CapturedAt is null ? 1 : 0,
                row.CapturedAt ?? row.ImportedAt,
                null,
                null,
                row.Id),
            AssetSort.ImportedAt => new AssetQueryKey(
                0,
                row.ImportedAt,
                null,
                null,
                row.Id),
            AssetSort.UpdatedAt => new AssetQueryKey(
                0,
                row.UpdatedAt,
                null,
                null,
                row.Id),
            AssetSort.Title => new AssetQueryKey(
                0,
                null,
                row.Title,
                null,
                row.Id),
            AssetSort.SizeBytes => new AssetQueryKey(
                0,
                null,
                null,
                row.SizeBytes,
                row.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(sort)),
        };

    private static AssetMetadata ToMetadata(MetadataProjection row)
    {
        Dictionary<string, string> source;
        try
        {
            source = JsonSerializer.Deserialize<Dictionary<string, string>>(
                row.SafeMetadataJson,
                JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            source = [];
        }

        var safe = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in SafeMetadataKeys)
        {
            if (source.TryGetValue(key, out string? value) &&
                !string.IsNullOrWhiteSpace(value) &&
                value.Length <= 500)
            {
                safe[key] = value;
            }
        }

        int? orientation = null;
        if (safe.TryGetValue("orientation", out string? orientationValue))
        {
            if (int.TryParse(orientationValue, out int numeric) &&
                Enum.IsDefined(typeof(ImageOrientation), numeric))
            {
                orientation = numeric;
            }
            else if (Enum.TryParse(
                orientationValue,
                ignoreCase: false,
                out ImageOrientation parsed))
            {
                orientation = (int)parsed;
            }
        }

        bool restricted = source.Keys.Any(key =>
                !SafeMetadataKeys.Contains(key, StringComparer.Ordinal)) ||
            !IsEmptyJsonObject(row.PrivateMetadataJson);
        return new AssetMetadata(
            row.AssetId,
            row.RevisionNumber,
            row.CapturedAt,
            orientation,
            safe.GetValueOrDefault("cameraMake"),
            safe.GetValueOrDefault("cameraModel"),
            safe.GetValueOrDefault("lensModel"),
            safe.GetValueOrDefault("colorSpace"),
            restricted,
            safe);
    }

    private static AssetFacetGroup ToFacetGroup(
        string name,
        AssetFacetValue[] values) =>
        new(
            name,
            values.Take(MaximumFacetValues).ToArray(),
            values.Length > MaximumFacetValues);

    private static bool ValidatePatch(AssetMetadataPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (!patch.HasTitle &&
            !patch.HasDescription &&
            !patch.HasVisibility &&
            !patch.HasCapturedAt)
        {
            return false;
        }

        if (patch.HasTitle &&
            (string.IsNullOrWhiteSpace(patch.Title) || patch.Title.Length > 500))
        {
            return false;
        }

        if (patch.HasDescription && patch.Description?.Length > 4_000)
        {
            return false;
        }

        return !patch.HasVisibility ||
            patch.Visibility is "Private" or "Tenant" or "Public";
    }

    private static void ApplyPatch(AssetRow asset, AssetMetadataPatch patch)
    {
        if (patch.HasTitle)
        {
            asset.Title = patch.Title!.Trim();
        }

        if (patch.HasDescription)
        {
            asset.Description = string.IsNullOrWhiteSpace(patch.Description)
                ? null
                : patch.Description.Trim();
        }

        if (patch.HasVisibility)
        {
            asset.Visibility = patch.Visibility!;
        }

        if (patch.HasCapturedAt)
        {
            asset.CapturedAtUtc = patch.CapturedAt?.ToUniversalTime();
            asset.CaptureSource = patch.CapturedAt is null ? null : "User";
            asset.CapturePrecision = patch.CapturedAt is null ? null : "Exact";
        }
    }

    private void EnsureTenant(AssetQueryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_context.TenantId != scope.TenantId)
        {
            throw new InvalidOperationException(
                "Asset queries cannot cross the established tenant scope.");
        }
    }

    private static string IdempotencySource(string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(key);
        try
        {
            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return $"asset-api:{digest[..32]}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string PatchFingerprint(
        long expectedVersion,
        AssetMetadataPatch patch)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new { expectedVersion, patch },
            JsonOptions);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string? ReadFingerprint(string changesJson)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateAudit>(
                changesJson,
                JsonOptions)?.Fingerprint;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string[] ChangedFields(AssetMetadataPatch patch)
    {
        var fields = new List<string>(4);
        if (patch.HasTitle)
        {
            fields.Add("title");
        }

        if (patch.HasDescription)
        {
            fields.Add("description");
        }

        if (patch.HasVisibility)
        {
            fields.Add("visibility");
        }

        if (patch.HasCapturedAt)
        {
            fields.Add("capturedAt");
        }

        return fields.ToArray();
    }

    private static bool IsEmptyJsonObject(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "{}", StringComparison.Ordinal);

    private sealed class AssetProjection
    {
        public Guid Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Visibility { get; init; } = string.Empty;
        public long RevisionNumber { get; init; }
        public Guid RevisionId { get; init; }
        public string ContentType { get; init; } = string.Empty;
        public string Format { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public long SizeBytes { get; init; }
        public DateTimeOffset? CapturedAt { get; init; }
        public DateTimeOffset ImportedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public bool Favorite { get; init; }
        public long Version { get; init; }
    }

    private sealed record MetadataProjection(
        Guid AssetId,
        long RevisionNumber,
        DateTimeOffset? CapturedAt,
        string SafeMetadataJson,
        string PrivateMetadataJson);

    private sealed record AssetTagProjection(
        Guid AssetId,
        Guid TagId,
        string Name,
        string? Color);

    private sealed record DeliveryProjection(
        Guid AssetId,
        Guid RequestId,
        string Kind,
        int Width,
        int Height,
        string ContentType,
        bool IsPublic,
        string PipelineId,
        string SourceSha256,
        string RecipeSha256,
        string Extension);

    private sealed record UpdateAudit(
        string Fingerprint,
        IReadOnlyList<string> Fields);
}
