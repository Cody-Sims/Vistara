using Vistara.Application.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Cookies;

public sealed class CookieSessionInvalidator
{
    private readonly ICookieSessionStore _store;
    private readonly IClock _clock;

    public CookieSessionInvalidator(ICookieSessionStore store, IClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InvalidateUserAsync(
        UserId userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _store.RevokeUserAsync(
            userId,
            _clock.UtcNow,
            cancellationToken);
    }

    public ValueTask InvalidateMembershipAsync(
        UserId userId,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _store.RevokeMembershipAsync(
            userId,
            tenantId,
            _clock.UtcNow,
            cancellationToken);
    }
}
