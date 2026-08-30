using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Domain.Common;
using Vistara.Domain.Jobs;
using Vistara.Worker.Runtime.Jobs;

namespace Vistara.Worker.Features.Lifecycle;

public sealed class LifecycleRestoreService(
    ILifecycleWorkerStore store,
    IClock clock)
{
    private readonly ILifecycleWorkerStore _store =
        store ?? throw new ArgumentNullException(nameof(store));
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));

    public async ValueTask<JobHandlerResult> ProcessAsync(
        LifecycleRestoreJobPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        DateTimeOffset now = _clock.UtcNow;
        if (now.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The lifecycle clock must return UTC.");
        }

        Result restored = await _store.RestoreAsync(
            payload,
            now,
            cancellationToken);
        return restored.IsSuccess
            ? JobHandlerResult.Success()
            : JobHandlerResult.Failed(
                new JobFailure(
                    restored.Error?.Category == ErrorCategory.Unavailable
                        ? JobFailureReason.ProviderUnavailable
                        : JobFailureReason.ProcessingFailed));
    }
}
