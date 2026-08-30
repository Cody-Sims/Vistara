using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Vistara.Contracts.Assets;
using Vistara.Contracts.Concurrency;
using Vistara.Contracts.Pagination;

namespace Vistara.Contracts.Lifecycle;

public sealed record TrashListQuery(
    [property: Range(1, CursorPageRequest.MaximumLimit)]
    [property: JsonPropertyName("limit")] int Limit = CursorPageRequest.DefaultLimit,
    [property: JsonPropertyName("cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SignedCursor? Cursor = null,
    [property: JsonPropertyName("sort")] string Sort = "deletedAt",
    [property: JsonPropertyName("direction")] string Direction = "desc");

public sealed record TrashAssetResponse(
    [property: JsonPropertyName("asset")] AssetSummaryResponse Asset,
    [property: JsonPropertyName("deletedAt")] DateTimeOffset DeletedAt,
    [property: JsonPropertyName("purgeAt")] DateTimeOffset PurgeAt,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("activeHoldCount")] int ActiveHoldCount,
    [property: JsonPropertyName("blockingReferenceCount")] int BlockingReferenceCount,
    [property: JsonPropertyName("estimatedReclaimBytes")] long EstimatedReclaimBytes);

public sealed class RestoreAssetsRequest
{
    [JsonConstructor]
    public RestoreAssetsRequest(IReadOnlyList<VersionedAssetReference> items)
    {
        Items = AssetContractValidation.CopyTargets(items, nameof(items));
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<VersionedAssetReference> Items { get; }
}

public sealed class CreatePurgeDryRunRequest
{
    [JsonConstructor]
    public CreatePurgeDryRunRequest(
        IReadOnlyList<VersionedAssetReference> items,
        string phase = "dryRun")
    {
        if (!string.Equals(phase, "dryRun", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The purge request phase must be 'dryRun'.",
                nameof(phase));
        }

        Items = AssetContractValidation.CopyTargets(items, nameof(items));
        Phase = phase;
    }

    [JsonPropertyName("phase")]
    public string Phase { get; }

    [JsonPropertyName("items")]
    public IReadOnlyList<VersionedAssetReference> Items { get; }
}

public sealed class ConfirmPurgeRequest
{
    [JsonConstructor]
    public ConfirmPurgeRequest(
        string dryRunDigest,
        bool acknowledgePermanentDeletion)
    {
        DryRunDigest = ContractGuards.RequiredText(
            dryRunDigest,
            nameof(dryRunDigest),
            256);
        if (!acknowledgePermanentDeletion)
        {
            throw new ArgumentException(
                "Permanent deletion must be explicitly acknowledged.",
                nameof(acknowledgePermanentDeletion));
        }

        AcknowledgePermanentDeletion = acknowledgePermanentDeletion;
    }

    [JsonPropertyName("dryRunDigest")]
    public string DryRunDigest { get; }

    [JsonPropertyName("acknowledgePermanentDeletion")]
    public bool AcknowledgePermanentDeletion { get; }
}

public sealed record PurgeDryRunResponse(
    [property: JsonPropertyName("batchId")] Guid BatchId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("dryRunDigest")] string DryRunDigest,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("eligibleCount")] int EligibleCount,
    [property: JsonPropertyName("estimatedReclaimBytes")] long EstimatedReclaimBytes,
    [property: JsonPropertyName("items")] IReadOnlyList<PurgeCandidateResponse> Items,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record PurgeCandidateResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("revisionNumber")] long RevisionNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("eligible")] bool Eligible,
    [property: JsonPropertyName("barriers")] IReadOnlyList<string> Barriers,
    [property: JsonPropertyName("sharedLinkImpact")] int SharedLinkImpact,
    [property: JsonPropertyName("estimatedReclaimBytes")] long EstimatedReclaimBytes);

public sealed record PurgeBatchResponse(
    [property: JsonPropertyName("batchId")] Guid BatchId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("requestedAt")] DateTimeOffset RequestedAt,
    [property: JsonPropertyName("approvedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ApprovedAt,
    [property: JsonPropertyName("startedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? StartedAt,
    [property: JsonPropertyName("completedAt")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("candidateCount")] int CandidateCount,
    [property: JsonPropertyName("eligibleCount")] int EligibleCount,
    [property: JsonPropertyName("processedCount")] int ProcessedCount,
    [property: JsonPropertyName("reclaimedBytes")] long ReclaimedBytes,
    [property: JsonPropertyName("items")] IReadOnlyList<PurgeItemResultResponse> Items,
    [property: JsonPropertyName("version")] ResourceVersion Version);

public sealed record PurgeItemResultResponse(
    [property: JsonPropertyName("assetId")] Guid AssetId,
    [property: JsonPropertyName("revisionNumber")] long RevisionNumber,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("reclaimedBytes")] long ReclaimedBytes,
    [property: JsonPropertyName("errorCode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode);
