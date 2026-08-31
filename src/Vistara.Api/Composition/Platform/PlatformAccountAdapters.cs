using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Tenancy;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Verifies local account passwords against the tenant-independent identity
/// catalog so sign-in can resolve a principal before a tenant scope exists.
/// </summary>
internal sealed class PlatformLocalCredentialVerifier(
    RelationalIdentityCatalog catalog,
    ILocalPasswordHasher hasher,
    DummyLocalPasswordVerifier dummy) : ILocalCredentialVerifier
{
    public async ValueTask<User?> VerifyAsync(
        string login,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        Result<NormalizedLogin> normalized = NormalizedLogin.Create(login);
        if (!normalized.TryGetValue(out NormalizedLogin value))
        {
            _ = dummy.ConsumeVerification(password);
            return null;
        }

        PersistedLocalCredential? credential =
            await catalog.FindCredentialAsync(value.Value, cancellationToken);
        if (credential is null)
        {
            // Absent logins run the same key derivation as present logins so a
            // caller cannot distinguish them by response time.
            _ = dummy.ConsumeVerification(password);
            return null;
        }

        if (!hasher.Verify(password, credential.PasswordHash))
        {
            return null;
        }

        return await catalog.FindUserAsync(credential.UserId, cancellationToken);
    }
}

/// <summary>
/// Bridges the browser session surface onto the existing cookie session
/// manager. Sign-in resolves memberships with one indexed identity-catalog
/// query and issues the session through a manager bound explicitly to the
/// selected tenant, so it works with no ambient tenant scope and when an
/// existing cookie belongs to a different tenant.
/// </summary>
internal sealed class PlatformBrowserSessionAdapter(
    ILocalCredentialVerifier verifier,
    PlatformLoginSessionFactory sessionFactory,
    RelationalIdentityCatalog catalog,
    IClock clock) : IBrowserSessionPort
{
    public async ValueTask<Result<BrowserSessionResult>> LoginAsync(
        BrowserLoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        User? user = await verifier.VerifyAsync(
            command.Login,
            command.Password,
            cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            return Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.InvalidCredentials);
        }

        IReadOnlyList<PersistedTenantMembership> candidates =
            await catalog.ListMembershipsAsync(user.Id.Value, cancellationToken);
        PersistedTenantMembership? selected = Select(candidates, command.TenantId);
        if (selected is null)
        {
            return Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.TenantUnavailable);
        }

        await RetireSessionAsync(command.ExistingSessionToken, cancellationToken);

        await using TenantScopedSessions scoped =
            sessionFactory.Create(selected.TenantId);
        TenantMembership? membership = await scoped.Memberships.FindAsync(
            new TenantId(selected.TenantId),
            user.Id,
            cancellationToken);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.TenantUnavailable);
        }

        IssuedBrowserSession issued = await scoped.Sessions.IssueAsync(
            user,
            membership,
            existingSessionToken: null,
            cancellationToken);
        return Result.Success(new BrowserSessionResult(
            Describe(
                user.Id.Value,
                user.Email.Value,
                user.DisplayName,
                selected.TenantId,
                membership.Role.ToString(),
                candidates),
            issued.Cookie.ToSetCookieHeader(),
            issued.AntiforgeryToken));
    }

    public async ValueTask<string> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        await RetireSessionAsync(sessionToken, cancellationToken);
        return sessionFactory.CreateDeletionCookie().ToSetCookieHeader();
    }

    public async ValueTask<string?> IssueAntiforgeryTokenAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        string digest = CookieTokenCryptography.ComputeDigest(sessionToken);
        Guid? owner =
            await sessionFactory.FindSessionTenantAsync(digest, cancellationToken);
        if (owner is not { } tenantId)
        {
            return null;
        }

        RelationalAuthenticationStore store = sessionFactory.CreateStore(tenantId);
        PersistedCookieSession? session =
            await store.FindCookieSessionAsync(digest, cancellationToken);
        if (session is null || session.RevokedAtUtc.HasValue)
        {
            return null;
        }

        string token = sessionFactory.CreateAntiforgeryToken();
        bool rotated = await store.RotateCookieAntiforgeryAsync(
            digest,
            session.Version,
            CookieTokenCryptography.ComputeDigest(token),
            clock.UtcNow,
            cancellationToken);
        return rotated ? token : null;
    }

    public async ValueTask<Result<CurrentUserView>> DescribeAsync(
        Guid tenantId,
        Guid userId,
        bool includeOtherTenants,
        CancellationToken cancellationToken)
    {
        PersistedIdentitySummary? summary =
            await catalog.FindSummaryAsync(userId, cancellationToken);
        if (summary is null)
        {
            return Result.Failure<CurrentUserView>(CookieAuthErrors.InvalidSession);
        }

        IReadOnlyList<PersistedTenantMembership> memberships =
            await catalog.ListMembershipsAsync(userId, cancellationToken);
        PersistedTenantMembership? current = memberships
            .SingleOrDefault(candidate => candidate.TenantId == tenantId);
        IReadOnlyList<PersistedTenantMembership> visible = includeOtherTenants
            ? memberships
            : memberships
                .Where(candidate => candidate.TenantId == tenantId)
                .ToArray();
        return Result.Success(Describe(
            summary.UserId,
            summary.Email,
            summary.DisplayName,
            current is null ? null : tenantId,
            current?.Role,
            visible));
    }

    private async ValueTask RetireSessionAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        string digest = CookieTokenCryptography.ComputeDigest(sessionToken);

        Guid? owner =
            await sessionFactory.FindSessionTenantAsync(digest, cancellationToken);
        if (owner is not { } tenantId)
        {
            return;
        }

        // The retired session may belong to a different tenant than the one
        // being signed in to, so it is revoked through a store bound to its own
        // tenant rather than through the ambient request scope.
        await sessionFactory
            .CreateStore(tenantId)
            .RevokeCookieSessionAsync(digest, clock.UtcNow, cancellationToken);
    }

    private static CurrentUserView Describe(
        Guid userId,
        string email,
        string displayName,
        Guid? tenantId,
        string? role,
        IReadOnlyList<PersistedTenantMembership> memberships) =>
        new(
            userId,
            email,
            displayName,
            tenantId,
            role,
            memberships
                .Select(membership => new CurrentUserTenantView(
                    membership.TenantId,
                    membership.Slug,
                    membership.Name,
                    membership.Role,
                    membership.MembershipStatus))
                .ToArray());

    private static PersistedTenantMembership? Select(
        IReadOnlyList<PersistedTenantMembership> candidates,
        Guid? requestedTenantId)
    {
        IEnumerable<PersistedTenantMembership> active = candidates.Where(candidate =>
            string.Equals(
                candidate.MembershipStatus,
                nameof(MembershipStatus.Active),
                StringComparison.Ordinal) &&
            string.Equals(
                candidate.TenantStatus,
                nameof(TenantStatus.Active),
                StringComparison.Ordinal));
        return requestedTenantId is { } requested
            ? active.SingleOrDefault(candidate => candidate.TenantId == requested)
            : active.FirstOrDefault();
    }
}

/// <summary>
/// Provisions the first tenant owner exactly once. Every row is written inside
/// one serializable transaction that also claims a database-enforced singleton
/// marker, so concurrent attempts with distinct slugs and emails still produce
/// a single winner and a failed attempt leaves nothing behind.
/// </summary>
internal sealed class PlatformFirstOwnerProvisioningAdapter(
    RelationalFirstOwnerProvisioningStore store,
    TenantFactory tenantFactory,
    IdentityFactory identityFactory,
    ILocalPasswordHasher hasher,
    IFirstOwnerProvisioningGuard guard,
    IUuid7Generator ids,
    IClock clock) : IFirstOwnerProvisioningPort
{
    public async ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
        FirstOwnerProvisioningCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        if (command.Password.Length < hasher.MinimumPasswordLength)
        {
            return Result.Failure<ProvisionedOwnerView>(ResultError.Validation(
                "setup.weak_password",
                $"The owner password must contain at least {hasher.MinimumPasswordLength} characters."));
        }

        Result<Tenant> tenantResult =
            tenantFactory.Create(command.TenantSlug, command.TenantName);
        if (!tenantResult.TryGetValue(out Tenant? tenant))
        {
            return Result.Failure<ProvisionedOwnerView>(tenantResult.Error!);
        }

        Result<User> userResult =
            identityFactory.CreateUser(command.Email, command.DisplayName);
        if (!userResult.TryGetValue(out User? user))
        {
            return Result.Failure<ProvisionedOwnerView>(userResult.Error!);
        }

        Result linked = identityFactory.LinkLocalIdentity(user, command.Email);
        if (linked.IsFailure)
        {
            return Result.Failure<ProvisionedOwnerView>(linked.Error!);
        }

        Result<TenantMembership> membershipResult = tenantFactory.InviteMember(
            tenant.Id,
            user.Id,
            TenantRole.TenantOwner);
        if (!membershipResult.TryGetValue(out TenantMembership? membership))
        {
            return Result.Failure<ProvisionedOwnerView>(membershipResult.Error!);
        }

        DateTimeOffset now = clock.UtcNow;
        Result activated = membership.Activate(now);
        if (activated.IsFailure)
        {
            return Result.Failure<ProvisionedOwnerView>(activated.Error!);
        }

        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain("tenantSlug", tenant.Slug.Value),
            AuditField.Plain("role", TenantRole.TenantOwner.ToString()),
        ]);
        var audit = new AuditRecord(
            new AuditEventId(ids.NewId()),
            new AuditTenantId(tenant.Id.Value),
            new AuditActor(AuditActorKind.System, "first-owner-provisioning"),
            "tenant.owner.provisioned",
            new AuditResource("tenant", tenant.Id.Value.ToString("D")),
            AuditChangeSummary.Empty,
            after.TryGetValue(out AuditChangeSummary? summary)
                ? summary
                : AuditChangeSummary.Empty,
            AuditOutcome.Succeeded,
            now);

        FirstOwnerProvisioningStatus status = await store.ProvisionAsync(
            new FirstOwnerProvisioningRequest(
                tenant,
                user,
                membership,
                user.LocalIdentities[0].Id.Value,
                hasher.Hash(command.Password),
                audit),
            guard.BeforeCommitAsync,
            cancellationToken);
        return status switch
        {
            FirstOwnerProvisioningStatus.Provisioned => Result.Success(
                new ProvisionedOwnerView(
                    tenant.Id.Value,
                    tenant.Slug.Value,
                    tenant.Name,
                    user.Id.Value,
                    user.Email.Value,
                    user.DisplayName,
                    TenantRole.TenantOwner.ToString())),
            FirstOwnerProvisioningStatus.AlreadyProvisioned =>
                Result.Failure<ProvisionedOwnerView>(ResultError.Conflict(
                    "setup.already_provisioned",
                    "The platform already has an owner and cannot be provisioned again.")),
            _ => Result.Failure<ProvisionedOwnerView>(ResultError.Conflict(
                "setup.provisioning_contended",
                "A concurrent provisioning attempt is in progress; retry the request.")),
        };
    }
}
