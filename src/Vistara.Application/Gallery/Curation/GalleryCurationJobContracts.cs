using System.Text.Json;
using System.Text.Json.Serialization;
using Vistara.Domain.Jobs;

namespace Vistara.Application.Gallery.Curation;

/// <summary>
/// Executes an already authorized bulk curation batch against the durable
/// gallery state. The worker owns no curation rules; it replays the batch the
/// API accepted through the authoritative store.
/// </summary>
public interface IGalleryCurationBulkExecutor
{
    ValueTask<IReadOnlyList<BulkCurationItemResult>> ExecuteBulkAsync(
        CurationActor actor,
        BulkCurationRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

/// <summary>
/// The durable payload of a queued bulk curation job. The envelope carries the
/// tenant and the authorized actor so a claimed job can never widen the
/// authority the request was accepted with.
/// </summary>
public sealed class GalleryCurationBulkJobPayload
{
    [JsonConstructor]
    public GalleryCurationBulkJobPayload(
        Guid tenantId,
        Guid actorId,
        bool actorCanManageAll,
        BulkCurationAction action,
        IReadOnlyList<BulkCurationTarget> items)
    {
        EnsureUuid7(tenantId, nameof(tenantId));
        EnsureUuid7(actorId, nameof(actorId));
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(items);
        if (!GalleryCurationBulkValidation.IsSupportedAction(action))
        {
            throw new ArgumentException(
                "The bulk curation action is not supported.",
                nameof(action));
        }

        BulkCurationTarget[] targets = [.. items];
        if (targets.Length is < 1 or > GalleryCurationBulkValidation.MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(items),
                targets.Length,
                "A bulk curation job must carry between 1 and 200 targets.");
        }

        foreach (BulkCurationTarget target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            EnsureUuid7(target.AssetId, nameof(items));
            if (target.Version < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(items),
                    target.Version,
                    "Bulk curation targets must carry a positive version.");
            }
        }

        if (targets.Select(target => target.AssetId).Distinct().Count() !=
            targets.Length)
        {
            throw new ArgumentException(
                "Bulk curation asset identifiers must be unique.",
                nameof(items));
        }

        TenantId = tenantId;
        ActorId = actorId;
        ActorCanManageAll = actorCanManageAll;
        Action = action;
        Items = targets;
    }

    public Guid TenantId { get; }

    public Guid ActorId { get; }

    public bool ActorCanManageAll { get; }

    public BulkCurationAction Action { get; }

    public IReadOnlyList<BulkCurationTarget> Items { get; }

    public CurationActor CreateActor() =>
        new(TenantId, ActorId, ActorCanManageAll);

    public BulkCurationRequest CreateRequest() => new(Items, Action);

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Bulk curation job identifiers must be UUIDv7 values.",
                parameterName);
        }
    }
}

/// <summary>
/// The single source of truth for the queued bulk curation job type, payload
/// version, and payload encoding shared by the producer and the worker.
/// </summary>
public static class GalleryCurationJobContracts
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

    /// <summary>
    /// Version 1 payloads carried no actor, so a claimed job could not be
    /// executed with the authority the request was accepted under. Only the
    /// actor-scoped envelope is supported.
    /// </summary>
    public const int PayloadVersion = 2;

    public static JobType BulkType { get; } = new("GalleryCurationBulk");

    public static string SerializeBulk(GalleryCurationBulkJobPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static bool TryParseBulk(
        JobType type,
        int payloadVersion,
        string json,
        out GalleryCurationBulkJobPayload? payload)
    {
        payload = null;
        if (type != BulkType ||
            payloadVersion != PayloadVersion ||
            string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<GalleryCurationBulkJobPayload>(
                json,
                JsonOptions);
            return payload is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

internal static class GalleryCurationBulkValidation
{
    internal const int MaximumBatchSize = 200;

    internal static bool IsSupportedAction(BulkCurationAction action) =>
        action.Kind switch
        {
            "addTag" or "removeTag" =>
                action.TagId is { } tagId &&
                IsUuid7(tagId) &&
                action.AlbumId is null &&
                action.Favorite is null,
            "addToAlbum" or "removeFromAlbum" =>
                action.AlbumId is { } albumId &&
                IsUuid7(albumId) &&
                action.TagId is null &&
                action.Favorite is null,
            "setFavorite" =>
                action.Favorite is not null &&
                action.TagId is null &&
                action.AlbumId is null,
            _ => false,
        };

    private static bool IsUuid7(Guid value) =>
        value != Guid.Empty && value.Version == 7;
}
