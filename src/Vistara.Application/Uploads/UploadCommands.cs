using Vistara.Domain.Common;
using Vistara.Domain.Uploads;

namespace Vistara.Application.Uploads;

public sealed record CreateUploadIntentCommand(
    Guid UploadSessionId,
    UploadIntent Intent,
    string StagingKey,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record RegisterUploadPartCommand(
    Guid TenantId,
    Guid UploadSessionId,
    UploadPart Part,
    DateTimeOffset RegisteredAtUtc);

public sealed record RequestUploadCommitCommand(
    Guid TenantId,
    Guid UploadSessionId,
    DateTimeOffset RequestedAtUtc);

public sealed record AbortUploadCommand(
    Guid TenantId,
    Guid UploadSessionId,
    DateTimeOffset RequestedAtUtc);

public sealed record UploadCommandResult(
    Guid TenantId,
    Guid UploadSessionId,
    UploadState State,
    long Version,
    DateTimeOffset UpdatedAtUtc);

public interface IUploadIntentCommandHandler
{
    ValueTask<Result<UploadCommandResult>> HandleAsync(
        CreateUploadIntentCommand command,
        CancellationToken cancellationToken);
}

public interface IUploadLifecycleCommandHandler
{
    ValueTask<Result<UploadCommandResult>> RegisterPartAsync(
        RegisterUploadPartCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<UploadCommandResult>> RequestCommitAsync(
        RequestUploadCommitCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result<UploadCommandResult>> AbortAsync(
        AbortUploadCommand command,
        CancellationToken cancellationToken);
}
