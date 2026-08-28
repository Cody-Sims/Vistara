namespace Vistara.Domain.Jobs;

public enum JobFailureReason
{
    ProcessingFailed = 1,
    ProviderUnavailable = 2,
    ProviderRateLimited = 3,
    ProviderAuthorizationDenied = 4,
    MediaDecodeFailed = 5,
    LeaseExpired = 6,
}

public sealed record JobFailure
{
    public JobFailure(JobFailureReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "The job failure reason is invalid.");
        }

        Reason = reason;
    }

    public JobFailureReason Reason { get; }

    public string Code => Reason switch
    {
        JobFailureReason.ProcessingFailed => "jobs.processing_failed",
        JobFailureReason.ProviderUnavailable => "jobs.provider_unavailable",
        JobFailureReason.ProviderRateLimited => "jobs.provider_rate_limited",
        JobFailureReason.ProviderAuthorizationDenied => "jobs.provider_authorization_denied",
        JobFailureReason.MediaDecodeFailed => "jobs.media_decode_failed",
        JobFailureReason.LeaseExpired => "jobs.lease_expired",
        _ => throw new InvalidOperationException("The job failure reason is invalid."),
    };

    public string Summary => Reason switch
    {
        JobFailureReason.ProcessingFailed => "The job could not be completed.",
        JobFailureReason.ProviderUnavailable => "The configured provider is unavailable.",
        JobFailureReason.ProviderRateLimited => "The configured provider rate limited the request.",
        JobFailureReason.ProviderAuthorizationDenied =>
            "Authorization was denied by the configured provider.",
        JobFailureReason.MediaDecodeFailed => "The media could not be decoded.",
        JobFailureReason.LeaseExpired => "The worker lease expired.",
        _ => throw new InvalidOperationException("The job failure reason is invalid."),
    };

    internal bool IsValid()
    {
        if (!Enum.IsDefined(Reason))
        {
            return false;
        }

        _ = Code;
        _ = Summary;
        return true;
    }
}
