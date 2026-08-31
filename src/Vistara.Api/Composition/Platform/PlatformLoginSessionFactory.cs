using Microsoft.Extensions.Logging;
using Vistara.Application.Common;
using Vistara.Auth.Cookies;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Repositories;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Builds a cookie session manager bound to one explicit tenant. Sign-in must
/// never depend on the ambient request tenant scope, because a browser may
/// arrive with no cookie at all or with a cookie for a different tenant.
/// </summary>
internal sealed class PlatformLoginSessionFactory(
    TenantDbContextFactory tenantContexts,
    AuthenticationCatalogDbContext authenticationCatalog,
    JwtRevocationCatalogDbContext revocationCatalog,
    IClock clock,
    IUuid7Generator ids,
    ICookieTokenSource tokens,
    CookieAuthOptions options,
    ILogger<PlatformCookieAuthAuditSink> auditLogger)
{
    internal RelationalAuthenticationStore CreateStore(Guid tenantId) =>
        new(
            authenticationCatalog,
            revocationCatalog,
            tenantContexts,
            new FixedTenantScope(tenantId));

    /// <summary>
    /// Resolves the owning tenant of an existing browser session without any
    /// tenant scope, using the tenant-independent routing table.
    /// </summary>
    internal ValueTask<Guid?> FindSessionTenantAsync(
        string sessionTokenDigest,
        CancellationToken cancellationToken) =>
        CreateStore(Guid.CreateVersion7())
            .FindCookieSessionTenantAsync(sessionTokenDigest, cancellationToken);

    internal BrowserCookie CreateDeletionCookie() => BrowserCookie.Delete(options);

    internal string CreateAntiforgeryToken() =>
        CookieAntiforgeryTokenFactory.Create(tokens);

    internal TenantScopedSessions Create(Guid tenantId)
    {
        RelationalAuthenticationStore store = CreateStore(tenantId);
        VistaraDbContext context = tenantContexts.Create(tenantId);
        var memberships = new TenantMembershipRepository(context);
        var manager = new CookieSessionManager(
            clock,
            ids,
            tokens,
            new UserRepository(context),
            memberships,
            new PlatformCookieSessionStore(store),
            options,
            new PlatformCookieAuthAuditSink(store, auditLogger),
            new TenantRepository(context));
        return new TenantScopedSessions(manager, memberships, context);
    }
}

internal sealed class TenantScopedSessions(
    CookieSessionManager sessions,
    TenantMembershipRepository memberships,
    VistaraDbContext context) : IAsyncDisposable
{
    internal CookieSessionManager Sessions { get; } = sessions;

    internal TenantMembershipRepository Memberships { get; } = memberships;

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
