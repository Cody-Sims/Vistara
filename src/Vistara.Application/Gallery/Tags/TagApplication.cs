using System.Globalization;
using System.Text;

namespace Vistara.Application.Gallery.Tags;

public sealed record TagUpdate(
    OptionalField<string> Name,
    OptionalField<string> Color);

public interface ITagApplication
{
    ValueTask<CurationResult<IReadOnlyList<TagSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        string? search,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<TagSnapshot>> CreateAsync(
        CurationActor actor,
        Guid tagId,
        string name,
        string? color,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<TagSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        TagUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    ValueTask<CurationResult<CuratedAssetSnapshot>> SetAssetTagAsync(
        CurationActor actor,
        Guid assetId,
        Guid tagId,
        long expectedAssetVersion,
        bool tagged,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public interface ITagCurationStore : ITagApplication;

public sealed class TagApplication(ITagCurationStore store) : ITagApplication
{
    private readonly ITagCurationStore _store =
        store ?? throw new ArgumentNullException(nameof(store));

    public ValueTask<CurationResult<IReadOnlyList<TagSnapshot>>> ListAsync(
        CurationActor actor,
        int limit,
        string? search,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (limit is < 1 or > CurationValidation.MaximumBatchSize)
        {
            return ValueTask.FromResult(
                CurationResult.Failure<IReadOnlyList<TagSnapshot>>(
                    CurationFailure.Invalid("tag_limit_invalid")));
        }

        return _store.ListAsync(
            actor,
            limit,
            search is null ? null : Normalize(search),
            cancellationToken);
    }

    public ValueTask<CurationResult<TagSnapshot>> CreateAsync(
        CurationActor actor,
        Guid tagId,
        string name,
        string? color,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        string displayName = DisplayName(name);
        if (!ValidMutation(tagId, idempotencyKey, now) ||
            displayName.Length is 0 or > 500 ||
            !ValidColor(color))
        {
            return ValueTask.FromResult(CurationResult.Failure<TagSnapshot>(
                CurationFailure.Invalid("tag_request_invalid")));
        }

        return _store.CreateAsync(
            actor,
            tagId,
            displayName,
            color,
            idempotencyKey,
            now,
            cancellationToken);
    }

    public ValueTask<CurationResult<TagSnapshot>> UpdateAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        TagUpdate update,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(update);
        if (!ValidMutation(tagId, idempotencyKey, now) ||
            expectedVersion <= 0 ||
            (update.Color.IsSpecified && !ValidColor(update.Color.Value)))
        {
            return ValueTask.FromResult(CurationResult.Failure<TagSnapshot>(
                CurationFailure.Invalid("tag_request_invalid")));
        }

        OptionalField<string> name = update.Name;
        if (name.IsSpecified)
        {
            string displayName = DisplayName(name.Value);
            if (displayName.Length is 0 or > 500)
            {
                return ValueTask.FromResult(CurationResult.Failure<TagSnapshot>(
                    CurationFailure.Invalid("tag_name_required")));
            }

            name = OptionalField.Specified(displayName);
        }

        return _store.UpdateAsync(
            actor,
            tagId,
            expectedVersion,
            update with { Name = name },
            idempotencyKey,
            now,
            cancellationToken);
    }

    public ValueTask<CurationResult<bool>> DeleteAsync(
        CurationActor actor,
        Guid tagId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return ValidMutation(tagId, idempotencyKey, now) && expectedVersion > 0
            ? _store.DeleteAsync(
                actor,
                tagId,
                expectedVersion,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<bool>(
                CurationFailure.Invalid("tag_request_invalid")));
    }

    public ValueTask<CurationResult<CuratedAssetSnapshot>> SetAssetTagAsync(
        CurationActor actor,
        Guid assetId,
        Guid tagId,
        long expectedAssetVersion,
        bool tagged,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        bool valid = ValidMutation(assetId, idempotencyKey, now) &&
            CurationValidation.IsUuid7(tagId) &&
            expectedAssetVersion > 0;
        return valid
            ? _store.SetAssetTagAsync(
                actor,
                assetId,
                tagId,
                expectedAssetVersion,
                tagged,
                idempotencyKey,
                now,
                cancellationToken)
            : ValueTask.FromResult(CurationResult.Failure<CuratedAssetSnapshot>(
                CurationFailure.Invalid("asset_tag_request_invalid")));
    }

    internal static string Normalize(string value) =>
        DisplayName(value).ToLower(CultureInfo.InvariantCulture);

    private static string DisplayName(string? value) =>
        CurationValidation.CollapseWhitespace(value)
            .Normalize(NormalizationForm.FormKC);

    private static bool ValidColor(string? color) =>
        color is null || color.Length <= 64;

    private static bool ValidMutation(
        Guid id,
        string idempotencyKey,
        DateTimeOffset now) =>
        CurationValidation.IsUuid7(id) &&
        CurationValidation.IsValidIdempotencyKey(idempotencyKey) &&
        CurationValidation.IsUtc(now);
}
