using Vistara.Domain.Common;
using Vistara.Domain.Uploads;

namespace Vistara.Application.Uploads;

public interface IUploadSessionRepository
{
    ValueTask<UploadSession?> GetAsync(
        Guid tenantId,
        Guid uploadSessionId,
        CancellationToken cancellationToken);

    ValueTask<UploadSession?> FindByIdempotencyAsync(
        Guid tenantId,
        Guid actorId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    ValueTask AddAsync(
        UploadSession session,
        CancellationToken cancellationToken);

    ValueTask SaveAsync(
        UploadSession session,
        long expectedVersion,
        CancellationToken cancellationToken);
}

public interface IReplayableUploadContent
{
    long Length { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

public sealed record UploadContentReceipt(
    Guid TenantId,
    Guid UploadSessionId,
    long BytesWritten,
    string? ProviderVersion);

public interface IUploadContentReceiver
{
    ValueTask<Result<UploadContentReceipt>> WriteAsync(
        Guid tenantId,
        Guid uploadSessionId,
        IReplayableUploadContent content,
        CancellationToken cancellationToken);
}
