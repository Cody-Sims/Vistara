using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Albums;
using Vistara.Application.Gallery.Favorites;
using Vistara.Application.Gallery.Tags;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;

namespace Vistara.Persistence.Gallery.Curation;

public sealed class RelationalGalleryCurationStore :
    IAlbumCurationStore,
    ITagCurationStore,
    IFavoriteCurationStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context;

    public RelationalGalleryCurationStore(VistaraDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async ValueTask<CurationResult<IReadOnlyList<AlbumSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        IQueryable<AlbumRow> query = _context.Albums.AsNoTracking();
        if (!actor.CanManageAll)
        {
            query = query.Where(album => album.OwnerId == actor.UserId);
        }

        AlbumRow[] rows = await query
            .OrderBy(album => album.Name)
            .ThenBy(album => album.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        IReadOnlyList<AlbumSnapshot> albums =
            await BuildAlbumsAsync(actor, rows, null, cancellationToken);
        return CurationResult.Success(albums);
    }

    public async ValueTask<CurationResult<AlbumSnapshot>> GetAsync(
        CurationActor actor,
        Guid albumId,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        AlbumRow? album = await _context.Albums
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        if (album is null)
        {
            return AlbumNotFound();
        }

        if (!CanManage(actor, album.OwnerId))
        {
            return CurationResult.Failure<AlbumSnapshot>(
                CurationFailure.Forbidden("album_forbidden"));
        }

        AlbumSnapshot snapshot = (await LoadAlbumAsync(
            actor,
            albumId,
            null,
            cancellationToken))!;
        return CurationResult.Success(snapshot);
    }

    public async ValueTask<CurationResult<AlbumSnapshot>> CreateAsync(
        CurationActor actor,
        Guid albumId,
        string name,
        string? description,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("album.create", name, description);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<AlbumSnapshot>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.ResourceId is { } existingId)
        {
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            AlbumSnapshot? existing = await LoadAlbumAsync(
                actor,
                existingId,
                null,
                cancellationToken);
            return existing is null
                ? AlbumNotFound()
                : CurationResult.Success(existing);
        }

        _context.Albums.Add(new AlbumRow
        {
            Id = albumId,
            TenantId = actor.TenantId,
            OwnerId = actor.UserId,
            Name = name,
            Description = description,
            SortMode = "Manual",
            Version = 1,
        });
        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:album:{albumId:D}",
            now);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return await RollbackAsync<AlbumSnapshot>(
                transaction,
                CurationFailure.Conflict("album_create_conflict"));
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAlbumAsync(
            actor,
            albumId,
            now,
            cancellationToken))!);
    }

    public async ValueTask<CurationResult<AlbumSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        AlbumUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("album.update", albumId, expectedVersion, update);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        CurationResult<AlbumSnapshot>? replay = await ReplayAlbumMutationAsync(
            actor,
            albumId,
            idempotencyKey,
            hash,
            now,
            transaction,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        AlbumRow? album = await _context.Albums
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        CurationFailure? access = CheckAlbum(actor, album, expectedVersion);
        if (access is not null)
        {
            return await RollbackAsync<AlbumSnapshot>(transaction, access);
        }

        if (update.CoverAssetId.IsSpecified &&
            update.CoverAssetId.Value is { } coverId)
        {
            bool member = await _context.AlbumItems.AnyAsync(
                item => item.AlbumId == albumId && item.AssetId == coverId,
                cancellationToken);
            if (!member)
            {
                return await RollbackAsync<AlbumSnapshot>(
                    transaction,
                    CurationFailure.Invalid("album_cover_not_member"));
            }
        }

        bool changed = false;
        if (update.Name.IsSpecified && album!.Name != update.Name.Value)
        {
            album.Name = update.Name.Value!;
            changed = true;
        }

        if (update.Description.IsSpecified && album!.Description != update.Description.Value)
        {
            album.Description = update.Description.Value;
            changed = true;
        }

        if (update.CoverAssetId.IsSpecified &&
            album!.CoverAssetId != update.CoverAssetId.Value)
        {
            album.CoverAssetId = update.CoverAssetId.Value;
            changed = true;
        }

        if (changed)
        {
            album!.Version++;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:album:{albumId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "album_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<AlbumSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAlbumAsync(
            actor,
            albumId,
            now,
            cancellationToken))!);
    }

    public async ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("album.delete", albumId, expectedVersion);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<bool>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.IsReplay)
        {
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return CurationResult.Success(true);
        }

        AlbumRow? album = await _context.Albums
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        CurationFailure? access = CheckAlbum(actor, album, expectedVersion);
        if (access is not null)
        {
            return await RollbackAsync<bool>(transaction, access);
        }

        Guid[] assetIds = await _context.AlbumItems
            .Where(item => item.AlbumId == albumId)
            .Select(item => item.AssetId)
            .ToArrayAsync(cancellationToken);
        AssetRow[] assets = await _context.Assets
            .Where(asset => assetIds.Contains(asset.Id))
            .ToArrayAsync(cancellationToken);
        foreach (AssetRow asset in assets)
        {
            asset.Version++;
            asset.UpdatedAtUtc = now;
        }

        _context.Albums.Remove(album!);
        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:album-deleted:{albumId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "album_version_conflict",
            cancellationToken);
        return saveFailure is null
            ? CurationResult.Success(true)
            : CurationResult.Failure<bool>(saveFailure);
    }

    public ValueTask<CurationResult<AlbumSnapshot>> AddItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ChangeAlbumItemsAsync(
            actor,
            albumId,
            expectedVersion,
            items,
            add: true,
            idempotencyKey,
            now,
            cancellationToken);

    public ValueTask<CurationResult<AlbumSnapshot>> RemoveItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ChangeAlbumItemsAsync(
            actor,
            albumId,
            expectedVersion,
            items,
            add: false,
            idempotencyKey,
            now,
            cancellationToken);

    public async ValueTask<CurationResult<AlbumSnapshot>> ReorderItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<AlbumItemPosition> items,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("album.reorder", albumId, expectedVersion, items);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        CurationResult<AlbumSnapshot>? replay = await ReplayAlbumMutationAsync(
            actor,
            albumId,
            idempotencyKey,
            hash,
            now,
            transaction,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        AlbumRow? album = await _context.Albums
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        CurationFailure? access = CheckAlbum(actor, album, expectedVersion);
        if (access is not null)
        {
            return await RollbackAsync<AlbumSnapshot>(transaction, access);
        }

        AlbumItemRow[] rows = await _context.AlbumItems
            .Where(item => item.AlbumId == albumId)
            .OrderBy(item => item.Position)
            .ToArrayAsync(cancellationToken);
        bool exactSet = rows.Length == items.Count &&
            rows.Select(row => row.AssetId).ToHashSet().SetEquals(
                items.Select(item => item.AssetId)) &&
            items.Select(item => item.Position).Order().SequenceEqual(
                Enumerable.Range(0, items.Count).Select(index => (long)index));
        if (!exactSet)
        {
            return await RollbackAsync<AlbumSnapshot>(
                transaction,
                CurationFailure.Invalid("album_order_invalid"));
        }

        bool changed = rows.Any(row =>
            items.Single(item => item.AssetId == row.AssetId).Position != row.Position);
        if (changed)
        {
            long offset = rows.Length == 0 ? 1 : rows.Max(row => row.Position) + rows.Length + 1;
            for (int index = 0; index < rows.Length; index++)
            {
                rows[index].Position = offset + index;
            }

            await _context.SaveChangesAsync(cancellationToken);
            foreach (AlbumItemRow row in rows)
            {
                row.Position = items.Single(item => item.AssetId == row.AssetId).Position;
            }

            album!.Version++;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:album:{albumId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "album_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<AlbumSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAlbumAsync(
            actor,
            albumId,
            now,
            cancellationToken))!);
    }

    ValueTask<CurationResult<IReadOnlyList<TagSnapshot>>> ITagApplication.ListAsync(
        CurationActor actor,
        int limit,
        string? search,
        CancellationToken cancellationToken) =>
        ListTagsAsync(actor, limit, search, cancellationToken);

    public async ValueTask<CurationResult<IReadOnlyList<TagSnapshot>>> ListTagsAsync(
        CurationActor actor,
        int limit,
        string? search,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        IQueryable<TagRow> query = _context.Tags.AsNoTracking();
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(tag => tag.NormalizedName.Contains(search));
        }

        TagRow[] rows = await query
            .OrderBy(tag => tag.NormalizedName)
            .ThenBy(tag => tag.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        Guid[] ids = rows.Select(row => row.Id).ToArray();
        Dictionary<Guid, long> counts = await _context.AssetTags
            .AsNoTracking()
            .Where(item => ids.Contains(item.TagId))
            .GroupBy(item => item.TagId)
            .Select(group => new { Id = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        IReadOnlyList<TagSnapshot> tags = rows.Select(row => new TagSnapshot(
            row.Id,
            row.DisplayName,
            row.Color,
            counts.GetValueOrDefault(row.Id),
            row.Version)).ToArray();
        return CurationResult.Success(tags);
    }

    ValueTask<CurationResult<TagSnapshot>> ITagApplication.CreateAsync(
        CurationActor actor,
        Guid tagId,
        string name,
        string? color,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        CreateTagAsync(
            actor,
            tagId,
            name,
            color,
            idempotencyKey,
            now,
            cancellationToken);

    public async ValueTask<CurationResult<TagSnapshot>> CreateTagAsync(
        CurationActor actor,
        Guid tagId,
        string name,
        string? color,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string normalized = NormalizeTag(name);
        string hash = Fingerprint("tag.create", name, normalized, color);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<TagSnapshot>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.ResourceId is { } existingId)
        {
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            TagSnapshot? existing = await LoadTagAsync(existingId, cancellationToken);
            return existing is null
                ? TagNotFound()
                : CurationResult.Success(existing);
        }

        bool duplicate = await _context.Tags.AnyAsync(
            tag => tag.NormalizedName == normalized,
            cancellationToken);
        if (duplicate)
        {
            return await RollbackAsync<TagSnapshot>(
                transaction,
                CurationFailure.Conflict("tag_name_conflict"));
        }

        _context.Tags.Add(new TagRow
        {
            Id = tagId,
            TenantId = actor.TenantId,
            DisplayName = name,
            NormalizedName = normalized,
            Color = color,
            Version = 1,
        });
        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:tag:{tagId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "tag_name_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<TagSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadTagAsync(tagId, cancellationToken))!);
    }

    ValueTask<CurationResult<TagSnapshot>> ITagApplication.UpdateAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        TagUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        UpdateTagAsync(
            actor,
            tagId,
            expectedVersion,
            update,
            idempotencyKey,
            now,
            cancellationToken);

    public async ValueTask<CurationResult<TagSnapshot>> UpdateTagAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        TagUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("tag.update", tagId, expectedVersion, update);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<TagSnapshot>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.IsReplay)
        {
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            TagSnapshot? existing = await LoadTagAsync(tagId, cancellationToken);
            return existing is null
                ? TagNotFound()
                : CurationResult.Success(existing);
        }

        TagRow? tag = await _context.Tags
            .SingleOrDefaultAsync(row => row.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return await RollbackAsync<TagSnapshot>(
                transaction,
                CurationFailure.NotFound("tag_not_found"));
        }

        if (tag.Version != expectedVersion)
        {
            return await RollbackAsync<TagSnapshot>(
                transaction,
                CurationFailure.Conflict("tag_version_conflict"));
        }

        bool changed = false;
        if (update.Name.IsSpecified)
        {
            string normalized = NormalizeTag(update.Name.Value!);
            bool duplicate = await _context.Tags.AnyAsync(
                row => row.Id != tagId && row.NormalizedName == normalized,
                cancellationToken);
            if (duplicate)
            {
                return await RollbackAsync<TagSnapshot>(
                    transaction,
                    CurationFailure.Conflict("tag_name_conflict"));
            }

            if (tag.DisplayName != update.Name.Value ||
                tag.NormalizedName != normalized)
            {
                tag.DisplayName = update.Name.Value!;
                tag.NormalizedName = normalized;
                changed = true;
            }
        }

        if (update.Color.IsSpecified && tag.Color != update.Color.Value)
        {
            tag.Color = update.Color.Value;
            changed = true;
        }

        if (changed)
        {
            tag.Version++;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:tag:{tagId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "tag_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<TagSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadTagAsync(tagId, cancellationToken))!);
    }

    ValueTask<CurationResult<bool>> ITagApplication.DeleteAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        DeleteTagAsync(
            actor,
            tagId,
            expectedVersion,
            idempotencyKey,
            now,
            cancellationToken);

    public async ValueTask<CurationResult<bool>> DeleteTagAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("tag.delete", tagId, expectedVersion);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<bool>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.IsReplay)
        {
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return CurationResult.Success(true);
        }

        TagRow? tag = await _context.Tags
            .SingleOrDefaultAsync(row => row.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return await RollbackAsync<bool>(
                transaction,
                CurationFailure.NotFound("tag_not_found"));
        }

        if (tag.Version != expectedVersion)
        {
            return await RollbackAsync<bool>(
                transaction,
                CurationFailure.Conflict("tag_version_conflict"));
        }

        Guid[] taggedIds = await _context.AssetTags
            .Where(item => item.TagId == tagId)
            .Select(item => item.AssetId)
            .ToArrayAsync(cancellationToken);
        AssetRow[] assets = await _context.Assets
            .Where(asset => taggedIds.Contains(asset.Id))
            .ToArrayAsync(cancellationToken);
        foreach (AssetRow asset in assets)
        {
            asset.Version++;
            asset.UpdatedAtUtc = now;
        }

        _context.Tags.Remove(tag);
        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:tag-deleted:{tagId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "tag_version_conflict",
            cancellationToken);
        return saveFailure is null
            ? CurationResult.Success(true)
            : CurationResult.Failure<bool>(saveFailure);
    }

    ValueTask<CurationResult<CuratedAssetSnapshot>> ITagApplication.SetAssetTagAsync(
        CurationActor actor,
        Guid assetId,
        Guid tagId,
        long expectedAssetVersion,
        bool tagged,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        SetAssetTagAsync(
            actor,
            assetId,
            tagId,
            expectedAssetVersion,
            tagged,
            idempotencyKey,
            now,
            cancellationToken);

    public async ValueTask<CurationResult<CuratedAssetSnapshot>> SetAssetTagAsync(
        CurationActor actor,
        Guid assetId,
        Guid tagId,
        long expectedAssetVersion,
        bool tagged,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint(
            "asset.tag",
            assetId,
            tagId,
            expectedAssetVersion,
            tagged);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        CurationResult<CuratedAssetSnapshot>? replay = await ReplayAssetMutationAsync(
            actor,
            assetId,
            idempotencyKey,
            hash,
            now,
            transaction,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        AssetRow? asset = await _context.Assets
            .SingleOrDefaultAsync(row => row.Id == assetId, cancellationToken);
        CurationFailure? access = CheckAsset(
            actor,
            asset,
            expectedAssetVersion,
            requireOwnership: true);
        if (access is not null)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(transaction, access);
        }

        bool tagExists = await _context.Tags.AnyAsync(
            tag => tag.Id == tagId,
            cancellationToken);
        if (!tagExists)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(
                transaction,
                CurationFailure.NotFound("tag_not_found"));
        }

        AssetTagRow? relation = await _context.AssetTags.SingleOrDefaultAsync(
            item => item.AssetId == assetId && item.TagId == tagId,
            cancellationToken);
        bool changed = tagged ? relation is null : relation is not null;
        if (tagged && relation is null)
        {
            _context.AssetTags.Add(new AssetTagRow
            {
                TenantId = actor.TenantId,
                AssetId = assetId,
                TagId = tagId,
                Source = "user",
            });
        }
        else if (!tagged && relation is not null)
        {
            _context.AssetTags.Remove(relation);
        }

        if (changed)
        {
            asset!.Version++;
            asset.UpdatedAtUtc = now;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:asset:{assetId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "asset_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<CuratedAssetSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAssetAsync(
            actor,
            assetId,
            cancellationToken))!);
    }

    public async ValueTask<CurationResult<CuratedAssetSnapshot>> SetAsync(
        CurationActor actor,
        Guid assetId,
        long expectedVersion,
        bool favorite,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("asset.favorite", assetId, expectedVersion, favorite);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        CurationResult<CuratedAssetSnapshot>? replay = await ReplayAssetMutationAsync(
            actor,
            assetId,
            idempotencyKey,
            hash,
            now,
            transaction,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        AssetRow? asset = await _context.Assets
            .SingleOrDefaultAsync(row => row.Id == assetId, cancellationToken);
        CurationFailure? access = CheckAsset(
            actor,
            asset,
            expectedVersion,
            requireOwnership: false);
        if (access is not null)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(transaction, access);
        }

        AssetFavoriteRow? relation = await _context.AssetFavorites.SingleOrDefaultAsync(
            item => item.UserId == actor.UserId && item.AssetId == assetId,
            cancellationToken);
        bool changed = favorite ? relation is null : relation is not null;
        if (favorite && relation is null)
        {
            _context.AssetFavorites.Add(new AssetFavoriteRow
            {
                TenantId = actor.TenantId,
                UserId = actor.UserId,
                AssetId = assetId,
                AddedAtUtc = now,
            });
        }
        else if (!favorite && relation is not null)
        {
            _context.AssetFavorites.Remove(relation);
        }

        if (changed)
        {
            asset!.Version++;
            asset.UpdatedAtUtc = now;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:asset:{assetId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "asset_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<CuratedAssetSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAssetAsync(
            actor,
            assetId,
            cancellationToken))!);
    }

    public async ValueTask<CurationResult<BulkCurationSubmission>> QueueBulkAsync(
        CurationActor actor,
        Guid jobId,
        BulkCurationRequest request,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint("bulk.queue", request);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<BulkCurationSubmission>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (replay.ResourceId is { } existingId)
        {
            JobRow? job = await _context.Jobs
                .AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == existingId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.ChangeTracker.Clear();
            return job is null
                ? CurationResult.Failure<BulkCurationSubmission>(
                    CurationFailure.NotFound("bulk_job_not_found"))
                : CurationResult.Success(new BulkCurationSubmission(
                    job.Id,
                    job.State == "Pending" ? "queued" : job.State.ToLowerInvariant(),
                    request.Items.Count,
                    job.CreatedAtUtc));
        }

        _context.Jobs.Add(new JobRow
        {
            Id = jobId,
            TenantId = actor.TenantId,
            Type = "GalleryCurationBulk",
            Payload = JsonSerializer.Serialize(request, JsonOptions),
            PayloadVersion = 1,
            DedupeKey = $"gallery-curation:{hash}",
            Priority = 0,
            MaxAttempts = 5,
            Attempts = 0,
            State = "Pending",
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            Version = 1,
        });
        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:bulk:{jobId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "bulk_queue_conflict",
            cancellationToken);
        return saveFailure is null
            ? CurationResult.Success(new BulkCurationSubmission(
                jobId,
                "queued",
                request.Items.Count,
                now))
            : CurationResult.Failure<BulkCurationSubmission>(saveFailure);
    }

    public async ValueTask<IReadOnlyList<BulkCurationItemResult>> ExecuteBulkAsync(
        CurationActor actor,
        BulkCurationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Items.Count,
                "A bulk curation request must contain between 1 and 200 items.");
        }

        if (request.Items.Select(item => item.AssetId).Distinct().Count() !=
            request.Items.Count)
        {
            throw new ArgumentException(
                "Bulk curation asset identifiers must be unique.",
                nameof(request));
        }

        var results = new List<BulkCurationItemResult>(request.Items.Count);
        foreach (BulkCurationTarget target in request.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurationResult<CuratedAssetSnapshot> result =
                await ExecuteBulkItemAsync(
                    actor,
                    target,
                    request.Action,
                    now,
                    cancellationToken);
            if (result.IsSuccess)
            {
                results.Add(new BulkCurationItemResult(
                    target.AssetId,
                    "succeeded",
                    result.Value!.Version,
                    null));
            }
            else
            {
                results.Add(new BulkCurationItemResult(
                    target.AssetId,
                    ToBulkStatus(result.Error!.Kind),
                    null,
                    result.Error.Code));
            }
        }

        return results;
    }

    private async ValueTask<CurationResult<AlbumSnapshot>> ChangeAlbumItemsAsync(
        CurationActor actor,
        Guid albumId,
        long expectedVersion,
        IReadOnlyList<VersionedAssetTarget> items,
        bool add,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        EnsureActor(actor);
        string hash = Fingerprint(
            add ? "album.items.add" : "album.items.remove",
            albumId,
            expectedVersion,
            items);
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        CurationResult<AlbumSnapshot>? replay = await ReplayAlbumMutationAsync(
            actor,
            albumId,
            idempotencyKey,
            hash,
            now,
            transaction,
            cancellationToken);
        if (replay is not null)
        {
            return replay;
        }

        AlbumRow? album = await _context.Albums
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        CurationFailure? albumAccess = CheckAlbum(actor, album, expectedVersion);
        if (albumAccess is not null)
        {
            return await RollbackAsync<AlbumSnapshot>(transaction, albumAccess);
        }

        Guid[] requestedIds = items.Select(item => item.AssetId).ToArray();
        AssetRow[] assets = await _context.Assets
            .Where(asset => requestedIds.Contains(asset.Id))
            .ToArrayAsync(cancellationToken);
        if (assets.Length != requestedIds.Length)
        {
            return await RollbackAsync<AlbumSnapshot>(
                transaction,
                CurationFailure.NotFound("asset_not_found"));
        }

        foreach (VersionedAssetTarget target in items)
        {
            AssetRow asset = assets.Single(row => row.Id == target.AssetId);
            CurationFailure? assetAccess = CheckAsset(
                actor,
                asset,
                target.Version,
                requireOwnership: true);
            if (assetAccess is not null)
            {
                return await RollbackAsync<AlbumSnapshot>(transaction, assetAccess);
            }
        }

        AlbumItemRow[] existing = await _context.AlbumItems
            .Where(item => item.AlbumId == albumId)
            .OrderBy(item => item.Position)
            .ToArrayAsync(cancellationToken);
        var byAsset = existing.ToDictionary(item => item.AssetId);
        bool changed = false;
        if (add)
        {
            long position = existing.Length;
            foreach (VersionedAssetTarget target in items)
            {
                if (byAsset.ContainsKey(target.AssetId))
                {
                    continue;
                }

                _context.AlbumItems.Add(new AlbumItemRow
                {
                    TenantId = actor.TenantId,
                    AlbumId = albumId,
                    AssetId = target.AssetId,
                    Position = position++,
                    AddedByUserId = actor.UserId,
                    AddedAtUtc = now,
                });
                assets.Single(asset => asset.Id == target.AssetId).Version++;
                assets.Single(asset => asset.Id == target.AssetId).UpdatedAtUtc = now;
                changed = true;
            }
        }
        else
        {
            AlbumItemRow[] removed = items
                .Where(item => byAsset.ContainsKey(item.AssetId))
                .Select(item => byAsset[item.AssetId])
                .ToArray();
            if (removed.Length > 0)
            {
                _context.AlbumItems.RemoveRange(removed);
                await _context.SaveChangesAsync(cancellationToken);
                AlbumItemRow[] remaining = existing
                    .Except(removed)
                    .OrderBy(item => item.Position)
                    .ToArray();
                for (int index = 0; index < remaining.Length; index++)
                {
                    remaining[index].Position = index;
                }

                foreach (AlbumItemRow row in removed)
                {
                    AssetRow asset = assets.Single(candidate => candidate.Id == row.AssetId);
                    asset.Version++;
                    asset.UpdatedAtUtc = now;
                }

                changed = true;
            }
        }

        if (changed)
        {
            album!.Version++;
        }

        AddIdempotency(
            actor,
            idempotencyKey,
            hash,
            $"gallery:album:{albumId:D}",
            now);
        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "album_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<AlbumSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAlbumAsync(
            actor,
            albumId,
            now,
            cancellationToken))!);
    }

    private async ValueTask<CurationResult<CuratedAssetSnapshot>> ExecuteBulkItemAsync(
        CurationActor actor,
        BulkCurationTarget target,
        BulkCurationAction action,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await BeginAsync(actor, cancellationToken);
        AssetRow? asset = await _context.Assets
            .SingleOrDefaultAsync(row => row.Id == target.AssetId, cancellationToken);
        bool ownershipRequired = action.Kind != "setFavorite";
        CurationFailure? access = CheckAsset(
            actor,
            asset,
            target.Version,
            ownershipRequired);
        if (access is not null)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(transaction, access);
        }

        CurationFailure? operationFailure = action.Kind switch
        {
            "addTag" => await ChangeBulkTagAsync(
                actor,
                asset!,
                action.TagId!.Value,
                add: true,
                now,
                cancellationToken),
            "removeTag" => await ChangeBulkTagAsync(
                actor,
                asset!,
                action.TagId!.Value,
                add: false,
                now,
                cancellationToken),
            "addToAlbum" => await ChangeBulkAlbumAsync(
                actor,
                asset!,
                action.AlbumId!.Value,
                add: true,
                now,
                cancellationToken),
            "removeFromAlbum" => await ChangeBulkAlbumAsync(
                actor,
                asset!,
                action.AlbumId!.Value,
                add: false,
                now,
                cancellationToken),
            "setFavorite" => await ChangeBulkFavoriteAsync(
                actor,
                asset!,
                action.Favorite!.Value,
                now,
                cancellationToken),
            _ => CurationFailure.Invalid("bulk_action_invalid"),
        };
        if (operationFailure is not null)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(
                transaction,
                operationFailure);
        }

        CurationFailure? saveFailure = await SaveAsync(
            transaction,
            "asset_version_conflict",
            cancellationToken);
        if (saveFailure is not null)
        {
            return CurationResult.Failure<CuratedAssetSnapshot>(saveFailure);
        }

        _context.ChangeTracker.Clear();
        return CurationResult.Success((await LoadAssetAsync(
            actor,
            target.AssetId,
            cancellationToken))!);
    }

    private async ValueTask<CurationFailure?> ChangeBulkTagAsync(
        CurationActor actor,
        AssetRow asset,
        Guid tagId,
        bool add,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool tagExists = await _context.Tags.AnyAsync(
            tag => tag.Id == tagId,
            cancellationToken);
        if (!tagExists)
        {
            return CurationFailure.NotFound("tag_not_found");
        }

        AssetTagRow? relation = await _context.AssetTags.SingleOrDefaultAsync(
            item => item.AssetId == asset.Id && item.TagId == tagId,
            cancellationToken);
        if (add && relation is null)
        {
            _context.AssetTags.Add(new AssetTagRow
            {
                TenantId = actor.TenantId,
                AssetId = asset.Id,
                TagId = tagId,
                Source = "user",
            });
            Touch(asset, now);
        }
        else if (!add && relation is not null)
        {
            _context.AssetTags.Remove(relation);
            Touch(asset, now);
        }

        return null;
    }

    private async ValueTask<CurationFailure?> ChangeBulkAlbumAsync(
        CurationActor actor,
        AssetRow asset,
        Guid albumId,
        bool add,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AlbumRow? album = await _context.Albums
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        if (album is null)
        {
            return CurationFailure.NotFound("album_not_found");
        }

        if (!CanManage(actor, album.OwnerId))
        {
            return CurationFailure.Forbidden("album_forbidden");
        }

        AlbumItemRow? relation = await _context.AlbumItems.SingleOrDefaultAsync(
            item => item.AlbumId == albumId && item.AssetId == asset.Id,
            cancellationToken);
        if (add && relation is null)
        {
            long nextPosition = await _context.AlbumItems
                .Where(item => item.AlbumId == albumId)
                .Select(item => (long?)item.Position)
                .MaxAsync(cancellationToken) + 1 ?? 0;
            _context.AlbumItems.Add(new AlbumItemRow
            {
                TenantId = actor.TenantId,
                AlbumId = albumId,
                AssetId = asset.Id,
                Position = nextPosition,
                AddedByUserId = actor.UserId,
                AddedAtUtc = now,
            });
            album.Version++;
            Touch(asset, now);
        }
        else if (!add && relation is not null)
        {
            _context.AlbumItems.Remove(relation);
            await _context.SaveChangesAsync(cancellationToken);
            AlbumItemRow[] remaining = await _context.AlbumItems
                .Where(item => item.AlbumId == albumId)
                .OrderBy(item => item.Position)
                .ToArrayAsync(cancellationToken);
            for (int index = 0; index < remaining.Length; index++)
            {
                remaining[index].Position = index;
            }

            album.Version++;
            Touch(asset, now);
        }

        return null;
    }

    private async ValueTask<CurationFailure?> ChangeBulkFavoriteAsync(
        CurationActor actor,
        AssetRow asset,
        bool favorite,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AssetFavoriteRow? relation = await _context.AssetFavorites.SingleOrDefaultAsync(
            item => item.UserId == actor.UserId && item.AssetId == asset.Id,
            cancellationToken);
        if (favorite && relation is null)
        {
            _context.AssetFavorites.Add(new AssetFavoriteRow
            {
                TenantId = actor.TenantId,
                UserId = actor.UserId,
                AssetId = asset.Id,
                AddedAtUtc = now,
            });
            Touch(asset, now);
        }
        else if (!favorite && relation is not null)
        {
            _context.AssetFavorites.Remove(relation);
            Touch(asset, now);
        }

        return null;
    }

    private async ValueTask<CurationResult<AlbumSnapshot>?> ReplayAlbumMutationAsync(
        CurationActor actor,
        Guid albumId,
        string idempotencyKey,
        string hash,
        DateTimeOffset now,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<AlbumSnapshot>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (!replay.IsReplay)
        {
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        AlbumSnapshot? album = await LoadAlbumAsync(
            actor,
            albumId,
            null,
            cancellationToken);
        return album is null
            ? AlbumNotFound()
            : CurationResult.Success(album);
    }

    private async ValueTask<CurationResult<CuratedAssetSnapshot>?> ReplayAssetMutationAsync(
        CurationActor actor,
        Guid assetId,
        string idempotencyKey,
        string hash,
        DateTimeOffset now,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        IdempotencyDecision replay = await CheckIdempotencyAsync(
            actor,
            idempotencyKey,
            hash,
            now,
            cancellationToken);
        if (replay.IsConflict)
        {
            return await RollbackAsync<CuratedAssetSnapshot>(
                transaction,
                CurationFailure.IdempotencyConflict("idempotency_key_reused"));
        }

        if (!replay.IsReplay)
        {
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        _context.ChangeTracker.Clear();
        CuratedAssetSnapshot? asset = await LoadAssetAsync(
            actor,
            assetId,
            cancellationToken);
        return asset is null
            ? CurationResult.Failure<CuratedAssetSnapshot>(
                CurationFailure.NotFound("asset_not_found"))
            : CurationResult.Success(asset);
    }

    private async ValueTask<IdempotencyDecision> CheckIdempotencyAsync(
        CurationActor actor,
        string key,
        string hash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IdempotencyRequestRow? row = await _context.IdempotencyRequests
            .SingleOrDefaultAsync(
                item => item.PrincipalId == actor.UserId &&
                    item.Key == IdempotencyStorageKey(key),
                cancellationToken);
        if (row is null)
        {
            return IdempotencyDecision.New;
        }

        if (row.ExpiresAtUtc <= now)
        {
            _context.IdempotencyRequests.Remove(row);
            await _context.SaveChangesAsync(cancellationToken);
            return IdempotencyDecision.New;
        }

        if (!string.Equals(row.RequestHash, hash, StringComparison.Ordinal))
        {
            return IdempotencyDecision.Conflict;
        }

        Guid? resourceId = ParseResourceId(row.ResponseReference);
        return new IdempotencyDecision(true, false, resourceId);
    }

    private void AddIdempotency(
        CurationActor actor,
        string key,
        string hash,
        string responseReference,
        DateTimeOffset now) =>
        _context.IdempotencyRequests.Add(new IdempotencyRequestRow
        {
            TenantId = actor.TenantId,
            PrincipalId = actor.UserId,
            Key = IdempotencyStorageKey(key),
            RequestHash = hash,
            ResponseReference = responseReference,
            ExpiresAtUtc = now.AddHours(24),
        });

    private async ValueTask<AlbumSnapshot?> LoadAlbumAsync(
        CurationActor actor,
        Guid albumId,
        DateTimeOffset? updatedAt,
        CancellationToken cancellationToken)
    {
        AlbumRow? album = await _context.Albums
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == albumId, cancellationToken);
        if (album is null)
        {
            return null;
        }

        IReadOnlyList<AlbumSnapshot> albums = await BuildAlbumsAsync(
            actor,
            [album],
            updatedAt,
            cancellationToken);
        return albums[0];
    }

    private async ValueTask<IReadOnlyList<AlbumSnapshot>> BuildAlbumsAsync(
        CurationActor actor,
        AlbumRow[] albums,
        DateTimeOffset? updatedAt,
        CancellationToken cancellationToken)
    {
        if (albums.Length == 0)
        {
            return [];
        }

        Guid[] albumIds = albums.Select(album => album.Id).ToArray();
        AlbumItemRow[] items = await _context.AlbumItems
            .AsNoTracking()
            .Where(item => albumIds.Contains(item.AlbumId))
            .OrderBy(item => item.AlbumId)
            .ThenBy(item => item.Position)
            .ToArrayAsync(cancellationToken);
        Dictionary<Guid, CuratedAssetSnapshot> assets =
            await LoadAssetsAsync(
                actor,
                items.Select(item => item.AssetId).Concat(
                    albums.Where(album => album.CoverAssetId is not null)
                        .Select(album => album.CoverAssetId!.Value)),
                cancellationToken);
        IReadOnlyDictionary<Guid, CuratedRenditionSnapshot> covers =
            await LoadCoversAsync(
                albums
                    .Where(album => album.CoverAssetId is not null)
                    .Select(album => album.CoverAssetId!.Value)
                    .ToArray(),
                cancellationToken);
        return albums.Select(album =>
        {
            AlbumItemRow[] albumItems = items
                .Where(item => item.AlbumId == album.Id)
                .OrderBy(item => item.Position)
                .ToArray();
            IReadOnlyList<AlbumItemSnapshot> snapshots = albumItems.Select(item =>
                new AlbumItemSnapshot(
                    assets[item.AssetId],
                    item.Position,
                    item.AddedAtUtc)).ToArray();
            CuratedRenditionSnapshot? cover =
                album.CoverAssetId is { } coverId &&
                assets.ContainsKey(coverId) &&
                covers.TryGetValue(coverId, out CuratedRenditionSnapshot? rendition)
                    ? rendition
                    : null;
            DateTimeOffset effectiveUpdatedAt = updatedAt ??
                (albumItems.Length == 0
                    ? DateTimeOffset.UnixEpoch
                    : albumItems.Max(item => item.AddedAtUtc));
            return new AlbumSnapshot(
                album.Id,
                album.Name,
                album.Description,
                cover,
                albumItems.Length,
                effectiveUpdatedAt,
                album.Version,
                snapshots);
        }).ToArray();
    }

    private async ValueTask<IReadOnlyDictionary<Guid, CuratedRenditionSnapshot>>
        LoadCoversAsync(
            Guid[] coverAssetIds,
            CancellationToken cancellationToken)
    {
        if (coverAssetIds.Length == 0)
        {
            return new Dictionary<Guid, CuratedRenditionSnapshot>();
        }

        CoverProjection[] candidates = await _context
            .Set<DerivativeRequestRow>()
            .AsNoTracking()
            .Where(row =>
                coverAssetIds.Contains(row.AssetId) &&
                row.State == "Ready" &&
                row.RepresentationContentType != null)
            .Select(row => new CoverProjection(
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
            .ToArrayAsync(cancellationToken);
        return candidates
            .GroupBy(candidate => candidate.AssetId)
            .ToDictionary(
                group => group.Key,
                group => ToCover(group
                    .OrderBy(PresetRank)
                    .ThenBy(candidate => candidate.Width * candidate.Height)
                    .ThenBy(candidate => candidate.RequestId)
                    .First()));
    }

    private static int PresetRank(CoverProjection candidate)
    {
        IReadOnlyList<string> preference =
            AssetRenditionDelivery.CoverPresetPreference;
        for (int rank = 0; rank < preference.Count; rank++)
        {
            if (string.Equals(preference[rank], candidate.Kind, StringComparison.Ordinal))
            {
                return rank;
            }
        }

        return preference.Count;
    }

    private static CuratedRenditionSnapshot ToCover(CoverProjection candidate) =>
        new(
            candidate.Kind,
            AssetRenditionDelivery.Path(
                candidate.AssetId,
                candidate.RequestId,
                candidate.IsPublic,
                candidate.PipelineId,
                candidate.SourceSha256,
                candidate.RecipeSha256,
                candidate.Extension),
            candidate.Width,
            candidate.Height,
            candidate.ContentType);

    private sealed record CoverProjection(
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

    private async ValueTask<TagSnapshot?> LoadTagAsync(
        Guid tagId,
        CancellationToken cancellationToken)
    {
        TagRow? tag = await _context.Tags
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == tagId, cancellationToken);
        if (tag is null)
        {
            return null;
        }

        long count = await _context.AssetTags.LongCountAsync(
            item => item.TagId == tagId,
            cancellationToken);
        return new TagSnapshot(
            tag.Id,
            tag.DisplayName,
            tag.Color,
            count,
            tag.Version);
    }

    private async ValueTask<CuratedAssetSnapshot?> LoadAssetAsync(
        CurationActor actor,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, CuratedAssetSnapshot> assets =
            await LoadAssetsAsync(actor, [assetId], cancellationToken);
        return assets.GetValueOrDefault(assetId);
    }

    private async ValueTask<Dictionary<Guid, CuratedAssetSnapshot>> LoadAssetsAsync(
        CurationActor actor,
        IEnumerable<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        AssetRow[] assets = await _context.Assets
            .AsNoTracking()
            .Where(asset => ids.Contains(asset.Id))
            .ToArrayAsync(cancellationToken);
        Guid[] revisionIds = assets
            .Where(asset => asset.CurrentRevisionId is not null)
            .Select(asset => asset.CurrentRevisionId!.Value)
            .ToArray();
        AssetRevisionRow[] revisions = await _context.AssetRevisions
            .AsNoTracking()
            .Where(revision => revisionIds.Contains(revision.Id))
            .ToArrayAsync(cancellationToken);
        Guid[] blobIds = revisions.Select(revision => revision.BlobId).ToArray();
        BlobRow[] blobs = await _context.Blobs
            .AsNoTracking()
            .Where(blob => blobIds.Contains(blob.Id))
            .ToArrayAsync(cancellationToken);
        var revisionsById = revisions.ToDictionary(revision => revision.Id);
        var blobsById = blobs.ToDictionary(blob => blob.Id);

        var tags = await (
            from assetTag in _context.AssetTags.AsNoTracking()
            join tag in _context.Tags.AsNoTracking() on assetTag.TagId equals tag.Id
            where ids.Contains(assetTag.AssetId)
            orderby tag.NormalizedName, tag.Id
            select new
            {
                assetTag.AssetId,
                Tag = new CuratedTagReference(tag.Id, tag.DisplayName, tag.Color),
            }).ToArrayAsync(cancellationToken);
        var albums = await (
            from item in _context.AlbumItems.AsNoTracking()
            join album in _context.Albums.AsNoTracking() on item.AlbumId equals album.Id
            where ids.Contains(item.AssetId)
            orderby album.Name, album.Id
            select new
            {
                item.AssetId,
                Album = new CuratedAlbumReference(album.Id, album.Name),
            }).ToArrayAsync(cancellationToken);
        HashSet<Guid> favorites = (await _context.AssetFavorites
                .AsNoTracking()
                .Where(item => item.UserId == actor.UserId && ids.Contains(item.AssetId))
                .Select(item => item.AssetId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();

        return assets.ToDictionary(
            asset => asset.Id,
            asset =>
            {
                AssetRevisionRow? revision = asset.CurrentRevisionId is { } revisionId
                    ? revisionsById.GetValueOrDefault(revisionId)
                    : null;
                BlobRow? blob = revision is null
                    ? null
                    : blobsById.GetValueOrDefault(revision.BlobId);
                return new CuratedAssetSnapshot(
                    asset.Id,
                    asset.Title,
                    asset.Description,
                    asset.Status,
                    asset.Visibility,
                    revision?.RevisionNumber ?? 0,
                    revision?.DetectedContentType ?? string.Empty,
                    revision?.DetectedFormat ?? string.Empty,
                    revision?.Width ?? 0,
                    revision?.Height ?? 0,
                    blob?.SizeBytes ?? 0,
                    asset.CapturedAtUtc,
                    asset.CreatedAtUtc,
                    asset.UpdatedAtUtc,
                    favorites.Contains(asset.Id),
                    tags.Where(item => item.AssetId == asset.Id)
                        .Select(item => item.Tag)
                        .ToArray(),
                    [],
                    asset.Version,
                    albums.Where(item => item.AssetId == asset.Id)
                        .Select(item => item.Album)
                        .ToArray());
            });
    }

    private static CurationFailure? CheckAlbum(
        CurationActor actor,
        AlbumRow? album,
        long expectedVersion)
    {
        if (album is null)
        {
            return CurationFailure.NotFound("album_not_found");
        }

        if (!CanManage(actor, album.OwnerId))
        {
            return CurationFailure.Forbidden("album_forbidden");
        }

        return album.Version == expectedVersion
            ? null
            : CurationFailure.Conflict("album_version_conflict");
    }

    private static CurationFailure? CheckAsset(
        CurationActor actor,
        AssetRow? asset,
        long expectedVersion,
        bool requireOwnership)
    {
        if (asset is null)
        {
            return CurationFailure.NotFound("asset_not_found");
        }

        bool owner = asset.OwnerId == actor.UserId;
        if (!owner && !actor.CanManageAll && asset.Visibility == "Private")
        {
            return CurationFailure.NotFound("asset_not_found");
        }

        if (requireOwnership && !owner && !actor.CanManageAll)
        {
            return CurationFailure.Forbidden("asset_forbidden");
        }

        return asset.Version == expectedVersion
            ? null
            : CurationFailure.Conflict("asset_version_conflict");
    }

    private void EnsureActor(CurationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (_context.TenantId != actor.TenantId)
        {
            throw new InvalidOperationException(
                "The curation actor does not match the active tenant scope.");
        }
    }

    private static bool CanManage(CurationActor actor, Guid ownerId) =>
        actor.CanManageAll || actor.UserId == ownerId;

    private ValueTask<IDbContextTransaction> BeginAsync(
        CurationActor actor,
        CancellationToken cancellationToken) =>
        TenantDatabaseTransaction.BeginAsync(
            _context,
            actor.TenantId,
            cancellationToken);

    private async ValueTask<CurationFailure?> SaveAsync(
        IDbContextTransaction transaction,
        string conflictCode,
        CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            return CurationFailure.Conflict(conflictCode);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _context.ChangeTracker.Clear();
            return CurationFailure.Conflict(conflictCode);
        }
    }

    private async ValueTask<CurationResult<T>> RollbackAsync<T>(
        IDbContextTransaction transaction,
        CurationFailure failure)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        _context.ChangeTracker.Clear();
        return CurationResult.Failure<T>(failure);
    }

    private static CurationResult<AlbumSnapshot> AlbumNotFound() =>
        CurationResult.Failure<AlbumSnapshot>(
            CurationFailure.NotFound("album_not_found"));

    private static CurationResult<TagSnapshot> TagNotFound() =>
        CurationResult.Failure<TagSnapshot>(
            CurationFailure.NotFound("tag_not_found"));

    private static string NormalizeTag(string value) =>
        value.Normalize(NormalizationForm.FormKC)
            .ToLower(CultureInfo.InvariantCulture);

    private static string Fingerprint(string operation, params object?[] values)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new { operation, values },
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string IdempotencyStorageKey(string key)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"gallery:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static Guid? ParseResourceId(string? responseReference)
    {
        if (string.IsNullOrEmpty(responseReference))
        {
            return null;
        }

        string value = responseReference[(responseReference.LastIndexOf(':') + 1)..];
        return Guid.TryParse(value, out Guid id) ? id : null;
    }

    private static string ToBulkStatus(CurationFailureKind kind) =>
        kind switch
        {
            CurationFailureKind.NotFound => "notFound",
            CurationFailureKind.Forbidden => "forbidden",
            CurationFailureKind.Conflict => "conflict",
            CurationFailureKind.Invalid => "invalid",
            CurationFailureKind.IdempotencyConflict => "conflict",
            CurationFailureKind.Unavailable => "failed",
            _ => "failed",
        };

    private static void Touch(AssetRow asset, DateTimeOffset now)
    {
        asset.Version++;
        asset.UpdatedAtUtc = now;
    }

    private readonly record struct IdempotencyDecision(
        bool IsReplay,
        bool IsConflict,
        Guid? ResourceId)
    {
        internal static IdempotencyDecision New => new(false, false, null);

        internal static IdempotencyDecision Conflict => new(false, true, null);
    }
}
