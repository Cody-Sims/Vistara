using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Gallery;
using Vistara.Application.Gallery.Curation;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Gallery;

/// <summary>
/// Applies a claimed bulk curation batch through the authoritative curation
/// store and records the per-item outcome so the durable job carries evidence
/// of what it changed.
/// </summary>
public sealed class GalleryCurationBulkService(
    IGalleryCurationBulkExecutor executor,
    IAuditWriter audit,
    IUuid7Generator ids,
    IClock clock)
{
    internal const string AuditAction = "gallery.curation.bulk";

    private readonly IGalleryCurationBulkExecutor _executor =
        executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly IAuditWriter _audit =
        audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly IUuid7Generator _ids =
        ids ?? throw new ArgumentNullException(nameof(ids));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<JobHandlerResult> ProcessAsync(
        Guid jobId,
        GalleryCurationBulkJobPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset now = _clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The gallery curation clock must return UTC.");
        }

        // Every item carries the version the caller observed, so a redelivered
        // batch conflicts instead of applying its effects a second time.
        IReadOnlyList<BulkCurationItemResult> results =
            await _executor.ExecuteBulkAsync(
                payload.CreateActor(),
                payload.CreateRequest(),
                now,
                cancellationToken);
        await AppendAuditAsync(jobId, payload, results, now, cancellationToken);
        return results.Any(result =>
                string.Equals(result.Status, "failed", StringComparison.Ordinal))
            ? JobHandlerResult.Failed(
                new JobFailure(JobFailureReason.ProviderUnavailable))
            : JobHandlerResult.Success();
    }

    private async ValueTask AppendAuditAsync(
        Guid jobId,
        GalleryCurationBulkJobPayload payload,
        IReadOnlyList<BulkCurationItemResult> results,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        bool succeeded = results.All(result =>
            string.Equals(result.Status, "succeeded", StringComparison.Ordinal));
        await _audit.AppendAsync(
            new AuditRecord(
                new AuditEventId(_ids.NewId()),
                new AuditTenantId(payload.TenantId),
                new AuditActor(AuditActorKind.User, payload.ActorId.ToString("D")),
                AuditAction,
                new AuditResource("gallery.bulk", jobId.ToString("D")),
                Requested(payload),
                Applied(results),
                succeeded ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                now),
            cancellationToken);
    }

    private static AuditChangeSummary Requested(
        GalleryCurationBulkJobPayload payload)
    {
        var fields = new List<AuditField>
        {
            AuditField.Plain("action", payload.Action.Kind),
            AuditField.Plain(
                "requested",
                payload.Items.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (payload.Action.TagId is { } tagId)
        {
            fields.Add(AuditField.Plain("tag", tagId.ToString("D")));
        }

        if (payload.Action.AlbumId is { } albumId)
        {
            fields.Add(AuditField.Plain("album", albumId.ToString("D")));
        }

        if (payload.Action.Favorite is { } favorite)
        {
            fields.Add(AuditField.Plain(
                "favorite",
                favorite ? "true" : "false"));
        }

        return Summarize(fields);
    }

    private static AuditChangeSummary Applied(
        IReadOnlyList<BulkCurationItemResult> results) =>
        Summarize(results.Select(result => AuditField.Plain(
            $"asset:{result.AssetId:D}",
            Describe(result))));

    private static string Describe(BulkCurationItemResult result) =>
        result.Version is { } version
            ? string.Concat(
                result.Status,
                ":v",
                version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : string.Concat(result.Status, ":", result.ErrorCode ?? "unknown");

    private static AuditChangeSummary Summarize(IEnumerable<AuditField> fields)
    {
        Vistara.Domain.Common.Result<AuditChangeSummary> summary =
            AuditChangeSummary.Create(fields);
        return summary.TryGetValue(out AuditChangeSummary? value)
            ? value
            : throw new InvalidOperationException(
                summary.Error?.Message ??
                "The bulk curation audit summary could not be built.");
    }
}
