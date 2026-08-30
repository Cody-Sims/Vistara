namespace Vistara.Application.Common.Storage;

public enum BlobStoreErrorCode
{
    Unsupported,
    NotFound,
    PreconditionFailed,
    InvalidRange,
    IntegrityMismatch,
    InvalidRequest,
    OutcomeUnknown,
}

public sealed class BlobStoreException : Exception
{
    public BlobStoreException(
        BlobStoreErrorCode code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Code = code;
    }

    public BlobStoreErrorCode Code { get; }
}
