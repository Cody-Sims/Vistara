using Vistara.Api.Features.Derivatives;
using Vistara.Api.Features.Events;
using Vistara.Application.Common.Events;
using Vistara.Persistence.Events;
using Vistara.Persistence.Media;

namespace Vistara.Api.Composition.Platform;

internal sealed class PlatformDerivativeAuthorizationPort(
    IPlatformTenantContext tenantContext,
    RelationalMediaCatalogStore media) : IDerivativeAuthorizationPort
{
    public ValueTask<DerivativeAccess> AuthorizeCatalogAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            TryReadPrincipal(context, out Guid tenantId, out _)
                ? DerivativeAccess.AuthorizedCatalog(tenantId)
                : Denied(context));
    }

    public async ValueTask<DerivativeAccess> AuthorizeAssetAsync(
        HttpContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        if (!TryReadPrincipal(context, out Guid tenantId, out Guid userId))
        {
            return Denied(context);
        }

        return await media.CanReadAssetAsync(
            tenantId,
            userId,
            assetId,
            cancellationToken)
            ? DerivativeAccess.AuthorizedAsset(tenantId, assetId)
            : DerivativeAccess.Denied(DerivativeAccessStatus.Concealed);
    }

    private bool TryReadPrincipal(
        HttpContext context,
        out Guid tenantId,
        out Guid userId)
    {
        string requiredScope = HttpMethods.IsPost(context.Request.Method)
            ? "assets.upload"
            : "assets.read";
        return PlatformPrincipalReader.TryRead(
            context.User,
            tenantContext,
            requiredScope,
            out tenantId,
            out userId);
    }

    private static DerivativeAccess Denied(HttpContext context) =>
        DerivativeAccess.Denied(
            context.User.Identity?.IsAuthenticated == true
                ? DerivativeAccessStatus.Forbidden
                : DerivativeAccessStatus.Unauthenticated);
}

internal sealed class PlatformEventStreamAuthorizationPort(
    IPlatformTenantContext tenantContext) : IEventStreamAuthorizationPort
{
    public ValueTask<EventStreamAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(EventStreamAccess.Unauthenticated());
        }

        return ValueTask.FromResult(
            PlatformPrincipalReader.TryRead(
                context.User,
                tenantContext,
                "assets.read",
                out Guid tenantId,
                out _)
                ? EventStreamAccess.Authorized(tenantId)
                : EventStreamAccess.Forbidden());
    }
}

internal sealed class PlatformEventStreamSource(
    RelationalEventStreamStore store) : IEventStreamSource
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(500);

    public async ValueTask<EventStreamBounds> GetBoundsAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        PersistedEventStreamBounds bounds = await store.GetBoundsAsync(
            tenantId,
            cancellationToken);
        return new EventStreamBounds(
            bounds.OldestAvailable,
            bounds.Latest);
    }

    public ValueTask<IReadOnlyList<EventEnvelope>> ReadReplayAsync(
        Guid tenantId,
        long afterSequence,
        int maximumCount,
        CancellationToken cancellationToken) =>
        store.ReadAsync(
            tenantId,
            afterSequence,
            maximumCount,
            cancellationToken);

    public async IAsyncEnumerable<EventEnvelope> ReadLiveAsync(
        Guid tenantId,
        long afterSequence,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        long cursor = afterSequence;
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<EventEnvelope> events = await store.ReadAsync(
                tenantId,
                cursor,
                maximumCount: 200,
                cancellationToken);
            if (events.Count == 0)
            {
                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }

            foreach (EventEnvelope envelope in events)
            {
                cursor = envelope.Metadata.Sequence.Value;
                yield return envelope;
            }
        }
    }
}
