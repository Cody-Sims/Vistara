using Vistara.Domain.Common;

namespace Vistara.Domain.Jobs;

public static class JobErrors
{
    public static ResultError LeaseConflict =>
        ResultError.Conflict("jobs.lease_conflict", "The job has an active lease owned by another worker.");

    public static ResultError LeaseExpired =>
        ResultError.Conflict("jobs.lease_expired", "The job lease has expired.");

    public static ResultError LeaseNotExpired =>
        ResultError.Conflict("jobs.lease_not_expired", "The job lease has not expired.");

    public static ResultError NotAvailable =>
        ResultError.Conflict("jobs.not_available", "The job is not available yet.");

    public static ResultError InvalidState =>
        ResultError.Conflict("jobs.invalid_state", "The requested transition is invalid for the job state.");

    public static ResultError AttemptLimitReached =>
        ResultError.Conflict("jobs.attempt_limit_reached", "The job has reached its attempt limit.");

    public static ResultError InvalidSnapshot =>
        ResultError.Validation("jobs.invalid_snapshot", "The persisted job state is invalid.");
}
