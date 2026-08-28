using Vistara.Domain.Assets;
using Vistara.Domain.Common;

namespace Vistara.Domain.Uploads;

public enum UploadStrategy
{
    Proxy,
    Direct,
    Multipart,
}

public enum UploadState
{
    Pending,
    UploadIssued,
    CommitRequested,
    Verifying,
    Promoting,
    Accepted,
    Aborted,
    Expired,
    Rejected,
    OutcomeUnknown,
    Reconciling,
}

public static class UploadStateMachine
{
    private static readonly HashSet<(UploadState Current, UploadState Target)> Transitions =
        CreateTransitions();

    public static bool CanTransition(UploadState current, UploadState target) =>
        Enum.IsDefined(current) &&
        Enum.IsDefined(target) &&
        Transitions.Contains((current, target));

    private static HashSet<(UploadState Current, UploadState Target)> CreateTransitions()
    {
        HashSet<(UploadState Current, UploadState Target)> transitions =
        [
            (UploadState.Pending, UploadState.UploadIssued),
            (UploadState.UploadIssued, UploadState.CommitRequested),
            (UploadState.CommitRequested, UploadState.Verifying),
            (UploadState.Verifying, UploadState.Promoting),
            (UploadState.Promoting, UploadState.Accepted),
            (UploadState.UploadIssued, UploadState.OutcomeUnknown),
            (UploadState.CommitRequested, UploadState.OutcomeUnknown),
            (UploadState.Verifying, UploadState.OutcomeUnknown),
            (UploadState.Promoting, UploadState.OutcomeUnknown),
            (UploadState.OutcomeUnknown, UploadState.Reconciling),
        ];
        UploadState[] preAcceptStates =
        [
            UploadState.Pending,
            UploadState.UploadIssued,
            UploadState.CommitRequested,
            UploadState.Verifying,
            UploadState.Promoting,
            UploadState.OutcomeUnknown,
            UploadState.Reconciling,
        ];

        foreach (UploadState state in preAcceptStates)
        {
            transitions.Add((state, UploadState.Aborted));
            transitions.Add((state, UploadState.Expired));
            transitions.Add((state, UploadState.Rejected));
        }

        return transitions;
    }
}

public sealed class UploadIntegrityExpectation
{
    public UploadIntegrityExpectation(
        long expectedSizeBytes,
        Sha256Checksum expectedSha256,
        MediaContentType declaredContentType)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedSizeBytes);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(declaredContentType);

        ExpectedSizeBytes = expectedSizeBytes;
        ExpectedSha256 = expectedSha256;
        DeclaredContentType = declaredContentType;
    }

    public long ExpectedSizeBytes { get; }

    public Sha256Checksum ExpectedSha256 { get; }

    public MediaContentType DeclaredContentType { get; }

    public Result Validate(ObservedUploadObject observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.SizeBytes != ExpectedSizeBytes)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.size_mismatch",
                "Observed object size does not match the upload intent."));
        }

        if (observed.Sha256 != ExpectedSha256)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.checksum_mismatch",
                "Observed SHA-256 does not match the upload intent."));
        }

        if (observed.ContentType != DeclaredContentType)
        {
            return Result.Failure(ResultError.Conflict(
                "uploads.content_type_mismatch",
                "Observed content type does not match the upload intent."));
        }

        return Result.Success();
    }
}

public sealed class ObservedUploadObject
{
    public ObservedUploadObject(
        long sizeBytes,
        Sha256Checksum sha256,
        MediaContentType contentType,
        string objectKey,
        string? providerVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeBytes);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        SizeBytes = sizeBytes;
        Sha256 = sha256;
        ContentType = contentType;
        ObjectKey = objectKey;
        ProviderVersion =
            string.IsNullOrWhiteSpace(providerVersion) ? null : providerVersion.Trim();
    }

    public long SizeBytes { get; }

    public Sha256Checksum Sha256 { get; }

    public MediaContentType ContentType { get; }

    public string ObjectKey { get; }

    public string? ProviderVersion { get; }
}
