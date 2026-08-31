using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;
using Vistara.Persistence.Repositories;

namespace Vistara.Persistence.Auth;

public enum AuthenticationMutationStatus
{
    Applied,
    NotFound,
    Conflict,
}

public sealed record PersistedApiKeyAuthentication(
    ApiKeyMetadata Metadata,
    TenantStatus TenantStatus);

public sealed record PersistedCookieSession(
    Guid Id,
    string SessionTokenDigest,
    string AntiforgeryTokenDigest,
    Guid UserId,
    Guid TenantId,
    TenantRole Role,
    long UserVersion,
    long MembershipVersion,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset AbsoluteExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long Version);

public sealed record PersistedJwtMembership(
    Guid UserId,
    Guid TenantId,
    string TenantStatus,
    string MembershipStatus,
    string Role);

public sealed record PersistedDeliveryGrant(
    Guid Id,
    long Version,
    Guid TenantId,
    Guid? SubjectId,
    Guid? ShareId,
    long? ShareVersion,
    Guid AssetId,
    Guid RevisionId,
    string RenditionKind,
    string RenditionIdentifier,
    string Permission,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc,
    string PepperVersionId,
    string TokenDigestHex,
    DateTimeOffset? RevokedAtUtc);

public sealed record PersistedAuthenticationRoute(
    Guid TenantId,
    Guid PrincipalId,
    Guid CredentialId);

public sealed record PersistedAuthenticationAuditEvent(
    Guid TenantId,
    Guid? ActorId,
    string ActorKind,
    string Action,
    string ResourceType,
    string ResourceIdentifier,
    string Outcome,
    DateTimeOffset OccurredAtUtc);

public sealed class RelationalAuthenticationStore(
    AuthenticationCatalogDbContext catalog,
    JwtRevocationCatalogDbContext revocations,
    TenantDbContextFactory tenantContexts,
    ITenantScope requestTenantScope)
{
    private readonly AuthenticationCatalogDbContext _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly JwtRevocationCatalogDbContext _revocations =
        revocations ?? throw new ArgumentNullException(nameof(revocations));
    private readonly TenantDbContextFactory _tenantContexts =
        tenantContexts ?? throw new ArgumentNullException(nameof(tenantContexts));
    private readonly ITenantScope _requestTenantScope =
        requestTenantScope ?? throw new ArgumentNullException(nameof(requestTenantScope));

    public async ValueTask<bool> AddApiKeyAsync(
        ApiKeyMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        Guid tenantId = metadata.TenantId.Value;
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        context.ApiKeys.Add(DomainMapper.ToRow(metadata));
        context.Add(Route(
            AuthenticationRouteKinds.ApiKey,
            ApiKeyLookupDigest(metadata.Id.Value),
            tenantId,
            metadata.OwnerId.Value,
            metadata.Id.Value,
            metadata.CreatedAt));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async ValueTask<PersistedApiKeyAuthentication?>
        FindApiKeyForAuthenticationAsync(
            Guid keyId,
            CancellationToken cancellationToken)
    {
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.ApiKey,
            ApiKeyLookupDigest(keyId),
            cancellationToken);
        route ??= await FindRouteAsync(
            AuthenticationRouteKinds.ApiKey,
            LegacyApiKeyLookupDigest(keyId),
            cancellationToken);
        if (route is null || route.CredentialId != keyId)
        {
            return null;
        }

        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            route.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(route.TenantId);
        ApiKeyRow? row = await context.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == keyId &&
                    candidate.OwnerId == route.PrincipalId,
                cancellationToken);
        TenantRow? tenant = await context.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);
        bool ownerActive = await context.Users
            .AsNoTracking()
            .AnyAsync(
                user =>
                    user.Id == route.PrincipalId &&
                    user.Status == UserStatus.Active.ToString(),
                cancellationToken);
        if (row is null ||
            tenant is null ||
            !ownerActive ||
            !Enum.TryParse(tenant.Status, out TenantStatus tenantStatus))
        {
            return null;
        }

        return new PersistedApiKeyAuthentication(
            DomainMapper.ToDomain(row),
            tenantStatus);
    }

    public async ValueTask<AuthenticationMutationStatus> RevokeApiKeyAsync(
        Guid tenantId,
        Guid keyId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        ApiKeyRow? row = await context.ApiKeys.SingleOrDefaultAsync(
            candidate => candidate.Id == keyId,
            cancellationToken);
        if (row is null)
        {
            return AuthenticationMutationStatus.NotFound;
        }

        if (!row.RevokedAtUtc.HasValue)
        {
            row.RevokedAtUtc = revokedAtUtc;
            row.UpdatedAtUtc = revokedAtUtc;
            row.Version = checked(row.Version + 1);
            await context.SaveChangesAsync(cancellationToken);
        }

        return AuthenticationMutationStatus.Applied;
    }

    public async ValueTask RecordApiKeyLastUsedAsync(
        Guid tenantId,
        Guid keyId,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken)
    {
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        _ = await context.ApiKeys
            .Where(row =>
                row.Id == keyId &&
                row.RevokedAtUtc == null &&
                (row.ExpiresAtUtc == null || row.ExpiresAtUtc > usedAtUtc) &&
                (row.LastUsedAtUtc == null || row.LastUsedAtUtc < usedAtUtc))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.LastUsedAtUtc, usedAtUtc)
                    .SetProperty(row => row.UpdatedAtUtc, usedAtUtc)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
    }

    public async ValueTask<bool> AddCookieSessionAsync(
        PersistedCookieSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            session.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(session.TenantId);
        context.Add(ToRow(session));
        context.Add(Route(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(session.SessionTokenDigest),
            session.TenantId,
            session.UserId,
            session.Id,
            session.IssuedAtUtc));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves which tenant owns a browser session without requiring a tenant
    /// scope, so a sign-in can retire a session that belongs to another tenant.
    /// The routing table is deliberately tenant independent.
    /// </summary>
    public async ValueTask<Guid?> FindCookieSessionTenantAsync(
        string sessionTokenDigest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTokenDigest);
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(sessionTokenDigest),
            cancellationToken);
        return route?.TenantId;
    }

    public async ValueTask<PersistedCookieSession?> FindCookieSessionAsync(
        string sessionTokenDigest,
        CancellationToken cancellationToken)
    {
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(sessionTokenDigest),
            cancellationToken);
        if (route is null)
        {
            return null;
        }

        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            route.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(route.TenantId);
        CookieSessionRow? row = await context.Set<CookieSessionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Id == route.CredentialId &&
                    candidate.UserId == route.PrincipalId &&
                    candidate.SessionTokenDigest == sessionTokenDigest,
                cancellationToken);
        return row is null ? null : ToPersisted(row);
    }

    public async ValueTask<bool> RotateCookieSessionAsync(
        string currentSessionTokenDigest,
        long expectedVersion,
        PersistedCookieSession replacement,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(currentSessionTokenDigest),
            cancellationToken);
        if (route is null || route.TenantId != replacement.TenantId)
        {
            return false;
        }

        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            route.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(route.TenantId);
        CookieSessionRow? current = await context.Set<CookieSessionRow>()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == route.CredentialId &&
                    row.SessionTokenDigest == currentSessionTokenDigest,
                cancellationToken);
        if (current is null ||
            current.RevokedAtUtc.HasValue ||
            current.Version != expectedVersion)
        {
            return false;
        }

        current.RevokedAtUtc = revokedAtUtc;
        current.Version = checked(current.Version + 1);
        context.Entry(current).Property(row => row.Version).OriginalValue =
            expectedVersion;
        context.Add(ToRow(replacement));
        context.Add(Route(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(replacement.SessionTokenDigest),
            replacement.TenantId,
            replacement.UserId,
            replacement.Id,
            replacement.IssuedAtUtc));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces only the antiforgery digest of a live session. The session
    /// token and cookie are untouched, so a reloaded browser can obtain a
    /// usable antiforgery token without signing in again.
    /// </summary>
    public async ValueTask<bool> RotateCookieAntiforgeryAsync(
        string sessionTokenDigest,
        long expectedVersion,
        string antiforgeryTokenDigest,
        DateTimeOffset rotatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTokenDigest);
        ArgumentException.ThrowIfNullOrWhiteSpace(antiforgeryTokenDigest);
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(sessionTokenDigest),
            cancellationToken);
        if (route is null)
        {
            return false;
        }

        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, route.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(route.TenantId);
        CookieSessionRow? current = await context.Set<CookieSessionRow>()
            .SingleOrDefaultAsync(
                row =>
                    row.Id == route.CredentialId &&
                    row.SessionTokenDigest == sessionTokenDigest,
                cancellationToken);
        if (current is null ||
            current.RevokedAtUtc.HasValue ||
            current.Version != expectedVersion)
        {
            return false;
        }

        current.AntiforgeryTokenDigest = antiforgeryTokenDigest;
        current.LastSeenAtUtc = rotatedAtUtc > current.LastSeenAtUtc
            ? rotatedAtUtc
            : current.LastSeenAtUtc;
        current.Version = checked(current.Version + 1);
        context.Entry(current).Property(row => row.Version).OriginalValue =
            expectedVersion;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async ValueTask RevokeCookieSessionAsync(
        string sessionTokenDigest,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.CookieSession,
            CookieLookupDigest(sessionTokenDigest),
            cancellationToken);
        if (route is null)
        {
            return;
        }

        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            route.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(route.TenantId);
        _ = await context.Set<CookieSessionRow>()
            .Where(row =>
                row.Id == route.CredentialId &&
                row.SessionTokenDigest == sessionTokenDigest &&
                row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RevokedAtUtc, revokedAtUtc)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
    }

    public async ValueTask RevokeUserCookieSessionsAsync(
        Guid userId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        Guid[] tenants = await _catalog.Routes
            .AsNoTracking()
            .Where(row =>
                row.Kind == AuthenticationRouteKinds.CookieSession &&
                row.PrincipalId == userId)
            .Select(row => row.RoutedTenantId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        foreach (Guid tenantId in tenants)
        {
            await using VistaraDbContext context =
                _tenantContexts.Create(tenantId);
            _ = await context.Set<CookieSessionRow>()
                .Where(row => row.UserId == userId && row.RevokedAtUtc == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.RevokedAtUtc, revokedAtUtc)
                        .SetProperty(row => row.Version, row => row.Version + 1),
                    cancellationToken);
        }
    }

    public async ValueTask RevokeMembershipCookieSessionsAsync(
        Guid userId,
        Guid tenantId,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        _ = await context.Set<CookieSessionRow>()
            .Where(row => row.UserId == userId && row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.RevokedAtUtc, revokedAtUtc)
                    .SetProperty(row => row.Version, row => row.Version + 1),
                cancellationToken);
    }

    public async ValueTask<PersistedJwtMembership?> FindJwtMembershipAsync(
        string issuer,
        string subject,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        Guid? userId = await context.ExternalIdentities
            .AsNoTracking()
            .Where(row => row.Issuer == issuer && row.Subject == subject)
            .Select(row => (Guid?)row.UserId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!userId.HasValue)
        {
            return null;
        }

        bool userActive = await context.Users
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.Id == userId.Value &&
                    row.Status == UserStatus.Active.ToString(),
                cancellationToken);
        if (!userActive)
        {
            return null;
        }

        var result = await (
            from tenant in context.Tenants.AsNoTracking()
            join membership in context.TenantMemberships.AsNoTracking()
                on tenant.Id equals membership.TenantId
            where membership.UserId == userId.Value
            select new
            {
                TenantStatus = tenant.Status,
                MembershipStatus = membership.Status,
                membership.Role,
            }).SingleOrDefaultAsync(cancellationToken);
        return result is null
            ? null
            : new PersistedJwtMembership(
                userId.Value,
                tenantId,
                result.TenantStatus,
                result.MembershipStatus,
                result.Role);
    }

    public ValueTask<bool> IsJwtRevokedAsync(
        string issuer,
        string jwtId,
        CancellationToken cancellationToken) =>
        new(_revocations.RevokedTokens
            .AsNoTracking()
            .AnyAsync(
                row => row.Issuer == issuer && row.Jti == jwtId,
                cancellationToken));

    public async ValueTask<bool> AddDeliveryGrantAsync(
        PersistedDeliveryGrant grant,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        TenantScopeGuard.EstablishOrValidate(
            _requestTenantScope,
            grant.TenantId);
        await using VistaraDbContext context =
            _tenantContexts.Create(grant.TenantId);
        context.Add(ToRow(grant));
        context.Add(Route(
            AuthenticationRouteKinds.DeliveryGrant,
            DeliveryLookupDigest(grant.TokenDigestHex),
            grant.TenantId,
            grant.SubjectId ?? grant.ShareId!.Value,
            grant.Id,
            grant.IssuedAtUtc));
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    public async ValueTask<PersistedAuthenticationRoute?>
        FindDeliveryGrantRouteAsync(
            string tokenDigestHex,
            CancellationToken cancellationToken)
    {
        PersistedAuthenticationRoute? route = await FindRouteAsync(
            AuthenticationRouteKinds.DeliveryGrant,
            DeliveryLookupDigest(tokenDigestHex),
            cancellationToken);
        if (route is not null)
        {
            TenantScopeGuard.EstablishOrValidate(
                _requestTenantScope,
                route.TenantId);
        }

        return route;
    }

    public async ValueTask<PersistedDeliveryGrant?> FindDeliveryGrantAsync(
        Guid grantId,
        CancellationToken cancellationToken)
    {
        Guid tenantId = TenantScopeGuard.RequireTenantId(_requestTenantScope);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        DeliveryGrantRow? row = await context.Set<DeliveryGrantRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == grantId,
                cancellationToken);
        return row is null ? null : ToPersisted(row);
    }

    public async ValueTask<PersistedDeliveryGrant?> RevokeDeliveryGrantAsync(
        Guid tenantId,
        Guid grantId,
        long expectedVersion,
        DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken)
    {
        TenantScopeGuard.EstablishOrValidate(_requestTenantScope, tenantId);
        await using VistaraDbContext context = _tenantContexts.Create(tenantId);
        DeliveryGrantRow? row = await context.Set<DeliveryGrantRow>()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == grantId,
                cancellationToken);
        if (row is null ||
            row.Version != expectedVersion ||
            row.RevokedAtUtc.HasValue)
        {
            return null;
        }

        row.RevokedAtUtc = revokedAtUtc;
        row.Version = checked(row.Version + 1);
        context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return ToPersisted(row);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
    }

    public async ValueTask WriteAuditAsync(
        PersistedAuthenticationAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        await using VistaraDbContext context =
            _tenantContexts.Create(auditEvent.TenantId);
        context.AuditEvents.Add(new AuditEventRow
        {
            Id = Guid.CreateVersion7(auditEvent.OccurredAtUtc),
            TenantId = auditEvent.TenantId,
            ActorKind = auditEvent.ActorKind,
            ActorIdentifier =
                auditEvent.ActorId?.ToString("D") ?? "[unknown]",
            Action = auditEvent.Action,
            ResourceType = auditEvent.ResourceType,
            ResourceIdentifier = auditEvent.ResourceIdentifier,
            Outcome = auditEvent.Outcome,
            OccurredAtUtc = auditEvent.OccurredAtUtc,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async ValueTask<PersistedAuthenticationRoute?> FindRouteAsync(
        string kind,
        string lookupDigest,
        CancellationToken cancellationToken)
    {
        AuthenticationRouteRow? row = await _catalog.Routes
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.Kind == kind &&
                    candidate.LookupDigest == lookupDigest,
                cancellationToken);
        return row is null
            ? null
            : new PersistedAuthenticationRoute(
                row.RoutedTenantId,
                row.PrincipalId,
                row.CredentialId);
    }

    private static AuthenticationRouteRow Route(
        string kind,
        string lookupDigest,
        Guid tenantId,
        Guid principalId,
        Guid credentialId,
        DateTimeOffset createdAtUtc) =>
        new()
        {
            Kind = kind,
            LookupDigest = lookupDigest,
            RoutedTenantId = tenantId,
            PrincipalId = principalId,
            CredentialId = credentialId,
            CreatedAtUtc = createdAtUtc,
        };

    private static CookieSessionRow ToRow(PersistedCookieSession session) =>
        new()
        {
            Id = session.Id,
            TenantId = session.TenantId,
            SessionTokenDigest = session.SessionTokenDigest,
            AntiforgeryTokenDigest = session.AntiforgeryTokenDigest,
            UserId = session.UserId,
            Role = session.Role.ToString(),
            UserVersion = session.UserVersion,
            MembershipVersion = session.MembershipVersion,
            IssuedAtUtc = session.IssuedAtUtc,
            LastSeenAtUtc = session.LastSeenAtUtc,
            IdleExpiresAtUtc = session.IdleExpiresAtUtc,
            AbsoluteExpiresAtUtc = session.AbsoluteExpiresAtUtc,
            RevokedAtUtc = session.RevokedAtUtc,
            Version = session.Version,
        };

    private static PersistedCookieSession ToPersisted(CookieSessionRow row) =>
        new(
            row.Id,
            row.SessionTokenDigest,
            row.AntiforgeryTokenDigest,
            row.UserId,
            row.TenantId,
            Enum.Parse<TenantRole>(row.Role),
            row.UserVersion,
            row.MembershipVersion,
            row.IssuedAtUtc,
            row.LastSeenAtUtc,
            row.IdleExpiresAtUtc,
            row.AbsoluteExpiresAtUtc,
            row.RevokedAtUtc,
            row.Version);

    private static DeliveryGrantRow ToRow(PersistedDeliveryGrant grant) =>
        new()
        {
            Id = grant.Id,
            TenantId = grant.TenantId,
            SubjectId = grant.SubjectId,
            ShareId = grant.ShareId,
            ShareVersion = grant.ShareVersion,
            AssetId = grant.AssetId,
            RevisionId = grant.RevisionId,
            RenditionKind = grant.RenditionKind,
            RenditionIdentifier = grant.RenditionIdentifier,
            Permission = grant.Permission,
            IssuedAtUtc = grant.IssuedAtUtc,
            NotBeforeUtc = grant.NotBeforeUtc,
            ExpiresAtUtc = grant.ExpiresAtUtc,
            PepperVersionId = grant.PepperVersionId,
            TokenDigestHex = grant.TokenDigestHex,
            RevokedAtUtc = grant.RevokedAtUtc,
            Version = grant.Version,
        };

    private static PersistedDeliveryGrant ToPersisted(DeliveryGrantRow row) =>
        new(
            row.Id,
            row.Version,
            row.TenantId,
            row.SubjectId,
            row.ShareId,
            row.ShareVersion,
            row.AssetId,
            row.RevisionId,
            row.RenditionKind,
            row.RenditionIdentifier,
            row.Permission,
            row.IssuedAtUtc,
            row.NotBeforeUtc,
            row.ExpiresAtUtc,
            row.PepperVersionId,
            row.TokenDigestHex,
            row.RevokedAtUtc);

    private static string ApiKeyLookupDigest(Guid keyId) =>
        LookupDigest("api-key", keyId.ToString("N"));

    private static string LegacyApiKeyLookupDigest(Guid keyId)
    {
        string normalized = keyId.ToString("N");
        return string.Concat(normalized, normalized);
    }

    private static string CookieLookupDigest(string sessionTokenDigest) =>
        LookupDigest("cookie-session", sessionTokenDigest);

    private static string DeliveryLookupDigest(string tokenDigestHex) =>
        LookupDigest("delivery-grant", tokenDigestHex);

    private static string LookupDigest(string kind, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            string.Concat("vistara:", kind, ":", value));
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
