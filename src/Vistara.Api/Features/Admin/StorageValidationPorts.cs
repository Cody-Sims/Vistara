namespace Vistara.Api.Features.Admin;

public enum StorageCandidateKind
{
    Filesystem,
    AzureBlob,
    S3,
}

public enum AzureCredentialKind
{
    /// <summary>Workload or managed identity; no secret is submitted.</summary>
    ManagedIdentity,
    AccountKey,
    SasToken,
}

public enum S3CredentialKind
{
    /// <summary>Static access key, optionally with a session token.</summary>
    AccessKey,

    /// <summary>
    /// No credential. Only accepted when the deployment already trusts the
    /// endpoint host, which is how an emulator is configured.
    /// </summary>
    Anonymous,
}

/// <summary>
/// A validated candidate configuration plus the credentials submitted for one
/// validation. This is deliberately not a record: the compiler-generated
/// <c>ToString</c> of a record prints every member, which would leak a
/// credential into any log, activity tag, or exception that formats it.
/// </summary>
public sealed class StorageValidationCandidate : IDisposable
{
    private bool _disposed;

    public StorageValidationCandidate(
        StorageCandidateKind kind,
        string provider,
        string? rootPath = null,
        Uri? endpoint = null,
        string? container = null,
        string? accountName = null,
        string? region = null,
        bool forcePathStyle = false,
        AzureCredentialKind azureCredential = AzureCredentialKind.ManagedIdentity,
        S3CredentialKind s3Credential = S3CredentialKind.Anonymous,
        RedactedSecret? accountKey = null,
        RedactedSecret? sasToken = null,
        RedactedSecret? accessKeyId = null,
        RedactedSecret? secretAccessKey = null,
        RedactedSecret? sessionToken = null)
    {
        Kind = kind;
        Provider = provider;
        RootPath = rootPath;
        Endpoint = endpoint;
        Container = container;
        AccountName = accountName;
        Region = region;
        ForcePathStyle = forcePathStyle;
        AzureCredential = azureCredential;
        S3Credential = s3Credential;
        AccountKey = accountKey;
        SasToken = sasToken;
        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
        SessionToken = sessionToken;
    }

    public StorageCandidateKind Kind { get; }

    public string Provider { get; }

    public string? RootPath { get; }

    public Uri? Endpoint { get; }

    public string? Container { get; }

    public string? AccountName { get; }

    public string? Region { get; }

    public bool ForcePathStyle { get; }

    public AzureCredentialKind AzureCredential { get; }

    public S3CredentialKind S3Credential { get; }

    public RedactedSecret? AccountKey { get; }

    public RedactedSecret? SasToken { get; }

    public RedactedSecret? AccessKeyId { get; }

    public RedactedSecret? SecretAccessKey { get; }

    public RedactedSecret? SessionToken { get; }

    /// <summary>Prints only non-secret shape, never a submitted credential.</summary>
    public override string ToString() =>
        $"StorageValidationCandidate {{ Provider = {Provider}, Container = {Container}, Credential = {RedactedSecret.Placeholder} }}";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        AccountKey?.Dispose();
        SasToken?.Dispose();
        AccessKeyId?.Dispose();
        SecretAccessKey?.Dispose();
        SessionToken?.Dispose();
        _disposed = true;
    }
}

public enum StorageCheckId
{
    Reachable,
    Authenticated,
    Read,
    Write,
    Delete,
}

public enum StorageCheckStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record StorageValidationCheck(
    StorageCheckId Id,
    StorageCheckStatus Status,
    string? Detail);

public sealed record StorageValidationOutcome(
    bool Valid,
    IReadOnlyList<StorageValidationCheck> Checks,
    string? Message)
{
    public static StorageValidationOutcome Rejected(
        string message,
        string? detail = null) =>
        new(
            false,
            [
                new StorageValidationCheck(
                    StorageCheckId.Reachable,
                    StorageCheckStatus.Failed,
                    detail),
                Skipped(StorageCheckId.Authenticated),
                Skipped(StorageCheckId.Read),
                Skipped(StorageCheckId.Write),
                Skipped(StorageCheckId.Delete),
            ],
            message);

    public static StorageValidationCheck Skipped(StorageCheckId id) =>
        new(id, StorageCheckStatus.Skipped, null);
}

/// <summary>
/// Accumulates the checks a probe performed and fills the remainder as skipped,
/// so every answer carries the full, ordered check list.
/// </summary>
public sealed class StorageProbeRecorder
{
    private static readonly StorageCheckId[] Order =
    [
        StorageCheckId.Reachable,
        StorageCheckId.Authenticated,
        StorageCheckId.Read,
        StorageCheckId.Write,
        StorageCheckId.Delete,
    ];

    private readonly Dictionary<StorageCheckId, StorageValidationCheck> _checks = [];

    public void Pass(StorageCheckId id) =>
        _checks[id] = new StorageValidationCheck(id, StorageCheckStatus.Passed, null);

    public void Skip(StorageCheckId id, string? detail = null) =>
        _checks[id] = new StorageValidationCheck(id, StorageCheckStatus.Skipped, detail);

    /// <summary>
    /// Records a failed check and answers with the finished, invalid outcome.
    /// <paramref name="detail"/> and <paramref name="message"/> must come from
    /// the fixed catalogue so no provider text or credential can escape.
    /// </summary>
    public StorageValidationOutcome Fail(
        StorageCheckId id,
        string detail,
        string message)
    {
        _checks[id] = new StorageValidationCheck(id, StorageCheckStatus.Failed, detail);
        return Complete(message);
    }

    public StorageValidationOutcome Complete(string? message = null)
    {
        StorageValidationCheck[] checks =
            [.. Order.Select(id =>
                _checks.TryGetValue(id, out StorageValidationCheck? check)
                    ? check
                    : StorageValidationOutcome.Skipped(id))];
        bool valid = checks.All(check => check.Status != StorageCheckStatus.Failed) &&
            checks.Any(check => check.Status == StorageCheckStatus.Passed);
        return new StorageValidationOutcome(valid, checks, message);
    }
}

/// <summary>
/// A one-shot provider client. Implementations create a client per validation
/// and dispose it, so no credential survives the request.
/// </summary>
public interface IStorageValidationClient : IAsyncDisposable
{
    /// <summary>
    /// Runs the minimal capability probe: authenticate, read the container or
    /// bucket listing, then write and delete one small object under the
    /// reserved probe prefix. Existing data is never touched.
    /// </summary>
    ValueTask<StorageValidationOutcome> ProbeAsync(
        string probeKey,
        CancellationToken cancellationToken);
}

public interface IStorageValidationClientFactory
{
    /// <summary>
    /// Builds a one-shot client from the candidate. The credential is read
    /// here and nowhere else.
    /// </summary>
    ValueTask<IStorageValidationClient> CreateAsync(
        StorageValidationCandidate candidate,
        CancellationToken cancellationToken);
}

public interface IStorageValidationPort
{
    ValueTask<StorageValidationOutcome> ValidateAsync(
        StorageValidationCandidate candidate,
        CancellationToken cancellationToken);
}

public static class StorageProbeNaming
{
    /// <summary>
    /// Reserved prefix for probe objects. Nothing outside it is read, written,
    /// or deleted.
    /// </summary>
    public const string Prefix = ".vistara-validate/";

    public static string CreateKey() => $"{Prefix}{Guid.NewGuid():N}.probe";
}

/// <summary>
/// The complete catalogue of text a validation may answer with. Provider
/// messages are never forwarded because they routinely embed endpoints,
/// account names, and signed credentials.
/// </summary>
public static class StorageValidationDetails
{
    public const string EndpointRejected =
        "The endpoint is not an allowed validation target.";

    public const string Unreachable = "The storage target could not be reached.";

    public const string CredentialRejected = "The credential was rejected.";

    public const string AmbientCredentialRefused =
        "Managed identity is only used against a first-party Azure endpoint.";

    public const string CredentialMissing =
        "No credential is available for this provider.";

    public const string ListDenied =
        "The container or bucket could not be listed with this credential.";

    public const string WriteDenied = "The probe object could not be written.";

    public const string DeleteDenied = "The probe object could not be deleted.";

    public const string PathMissing = "The directory does not exist.";

    public const string NoCredentialNeeded = "A directory needs no credential.";

    public const string TimedOut =
        "The provider did not answer within the validation timeout.";

    public const string RejectedMessage =
        "The storage settings were rejected.";

    public const string TimeoutMessage =
        "The storage target did not answer in time.";

    public const string ValidMessage =
        "The storage settings are usable with the supplied credential.";
}
