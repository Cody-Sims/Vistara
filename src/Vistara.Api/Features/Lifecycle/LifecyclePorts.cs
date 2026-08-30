using Microsoft.AspNetCore.Http;
using Vistara.Application.Lifecycle;

namespace Vistara.Api.Features.Lifecycle;

public enum LifecycleApiOperation
{
    ListTrash,
    Restore,
    PurgeDryRun,
    PurgeConfirm,
    PurgeStatus,
}

public enum LifecycleAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
    Concealed,
}

public sealed record LifecycleAccess
{
    private LifecycleAccess(
        LifecycleAccessStatus status,
        LifecycleActorContext? actor)
    {
        Status = status;
        Actor = actor;
    }

    public LifecycleAccessStatus Status { get; }

    public LifecycleActorContext? Actor { get; }

    public static LifecycleAccess Authorized(LifecycleActorContext actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(LifecycleAccessStatus.Authorized, actor);
    }

    public static LifecycleAccess Denied(LifecycleAccessStatus status)
    {
        if (status == LifecycleAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, null);
    }
}

public interface ILifecycleAuthorizationPort
{
    ValueTask<LifecycleAccess> AuthorizeAsync(
        HttpContext context,
        LifecycleApiOperation operation,
        CancellationToken cancellationToken);
}

public sealed record LifecycleCursor(
    DateTimeOffset DeletedAtUtc,
    Guid AssetId,
    bool Descending);

public interface ILifecycleCursorCodec
{
    string Encode(LifecycleCursor cursor);

    bool TryDecode(string value, out LifecycleCursor? cursor);
}
