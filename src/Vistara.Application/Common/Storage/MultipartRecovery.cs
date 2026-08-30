namespace Vistara.Application.Common.Storage;

public enum MultipartInventoryState
{
    Active,
    Completed,
    Aborted,
    Missing,
}

public sealed record MultipartInventory(
    MultipartInventoryState State,
    IReadOnlyList<UploadedPart> Parts,
    BlobHead? CompletedHead = null);

public interface IDurableMultipartBlobStore
{
    /// <summary>
    /// Returns the same provider session for an issuance ID across retries,
    /// process restarts, and replicas.
    /// </summary>
    ValueTask<MultipartSession> GetOrCreateMultipartAsync(
        string issuanceId,
        MultipartRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns provider-observed state and parts; claimed parts are correlation
    /// input and must not be echoed without provider verification.
    /// </summary>
    ValueTask<MultipartInventory> InspectMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> claimedParts,
        CancellationToken cancellationToken);
}
