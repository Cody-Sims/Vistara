namespace Vistara.Api.Features.Admin;

public enum StorageCandidateKind
{
    Filesystem,
    Azure,
    S3,
}

/// <summary>
/// A validated, non-secret description of what to probe. Credentials are never
/// represented here, so nothing downstream can persist or log one.
/// </summary>
public sealed record StorageValidationTarget(
    StorageCandidateKind Kind,
    string Provider,
    string? RootPath,
    Uri? Endpoint,
    string? Container);

public sealed record StorageValidationOutcome(
    bool Reachable,
    string Code,
    string Message)
{
    public static StorageValidationOutcome Reached { get; } =
        new(true, "storage.reachable", "The storage target answered.");

    public static StorageValidationOutcome Unreachable { get; } =
        new(false, "storage.unreachable", "The storage target did not answer.");

    public static StorageValidationOutcome Denied { get; } =
        new(false, "storage.denied", "The storage target refused the request.");

    public static StorageValidationOutcome TimedOut { get; } =
        new(false, "storage.timed_out", "The storage target did not answer in time.");
}

/// <summary>
/// Performs one bounded reachability probe. Implementations must never return
/// provider text, because it routinely carries endpoints and credentials.
/// </summary>
public interface IStorageValidationProbe
{
    ValueTask<StorageValidationOutcome> ProbeAsync(
        StorageValidationTarget target,
        CancellationToken cancellationToken);
}

public interface IStorageValidationPort
{
    ValueTask<StorageValidationOutcome> ValidateAsync(
        StorageValidationTarget target,
        CancellationToken cancellationToken);
}
