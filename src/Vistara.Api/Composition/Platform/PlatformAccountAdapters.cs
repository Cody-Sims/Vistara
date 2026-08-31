using Microsoft.EntityFrameworkCore;
using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence;
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
/// manager, credential verifier, tenant directory, and membership repository.
/// </summary>
internal sealed class PlatformBrowserSessionAdapter(
    ILocalCredentialVerifier verifier,
    CookieSessionManager sessions,
    RelationalTenantDirectory directory,
    RelationalIdentityCatalog catalog,
    ITenantMembershipRepository memberships,
    IMutableTenantScope tenantScope) : IBrowserSessionPort
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
            await directory.ListForUserAsync(user.Id.Value, cancellationToken);
        PersistedTenantMembership? selected = Select(candidates, command.TenantId);
        if (selected is null)
        {
            return Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.TenantUnavailable);
        }

        tenantScope.Establish(selected.TenantId);
        TenantMembership? membership = await memberships.FindAsync(
            new TenantId(selected.TenantId),
            user.Id,
            cancellationToken);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return Result.Failure<BrowserSessionResult>(
                CookieAuthErrors.TenantUnavailable);
        }

        IssuedBrowserSession issued = await sessions.IssueAsync(
            user,
            membership,
            command.ExistingSessionToken,
            cancellationToken);
        return Result.Success(new BrowserSessionResult(
            Describe(user, selected.TenantId, membership.Role.ToString(), candidates),
            issued.Cookie.ToSetCookieHeader(),
            issued.AntiforgeryToken));
    }

    public async ValueTask<string> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        BrowserCookie cookie =
            await sessions.LogoutAsync(sessionToken, cancellationToken);
        return cookie.ToSetCookieHeader();
    }

    public async ValueTask<Result<CurrentUserView>> DescribeAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        PersistedIdentitySummary? summary =
            await catalog.FindSummaryAsync(userId, cancellationToken);
        if (summary is null)
        {
            return Result.Failure<CurrentUserView>(CookieAuthErrors.InvalidSession);
        }

        IReadOnlyList<PersistedTenantMembership> candidates =
            await directory.ListForUserAsync(userId, cancellationToken);
        PersistedTenantMembership? current = candidates
            .SingleOrDefault(candidate => candidate.TenantId == tenantId);
        return Result.Success(new CurrentUserView(
            summary.UserId,
            summary.Email,
            summary.DisplayName,
            current is null ? null : tenantId,
            current?.Role,
            candidates
                .Select(candidate => new CurrentUserTenantView(
                    candidate.TenantId,
                    candidate.Slug,
                    candidate.Name,
                    candidate.Role,
                    candidate.MembershipStatus))
                .ToArray()));
    }

    private static CurrentUserView Describe(
        User user,
        Guid tenantId,
        string role,
        IReadOnlyList<PersistedTenantMembership> candidates) =>
        new(
            user.Id.Value,
            user.Email.Value,
            user.DisplayName,
            tenantId,
            role,
            candidates
                .Select(candidate => new CurrentUserTenantView(
                    candidate.TenantId,
                    candidate.Slug,
                    candidate.Name,
                    candidate.Role,
                    candidate.MembershipStatus))
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
/// Provisions the first tenant owner exactly once. The route fails closed as
/// soon as any identity exists, so it cannot be replayed to escalate.
/// </summary>
internal sealed class PlatformFirstOwnerProvisioningAdapter(
    RelationalIdentityCatalog catalog,
    ITenantRepository tenants,
    IUserRepository users,
    ITenantMembershipRepository memberships,
    TenantFactory tenantFactory,
    IdentityFactory identityFactory,
    ILocalPasswordHasher hasher,
    IAuditWriter audit,
    IMutableTenantScope tenantScope,
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

        if (await catalog.HasAnyUserAsync(cancellationToken))
        {
            return Result.Failure<ProvisionedOwnerView>(AlreadyProvisioned);
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

        Result activated = membership.Activate(clock.UtcNow);
        if (activated.IsFailure)
        {
            return Result.Failure<ProvisionedOwnerView>(activated.Error!);
        }

        tenantScope.Establish(tenant.Id.Value);
        string passwordHash = hasher.Hash(command.Password);
        try
        {
            await tenants.AddAsync(tenant, cancellationToken);
            await users.AddAsync(user, cancellationToken);
            await memberships.AddAsync(membership, cancellationToken);
            await catalog.SetPasswordAsync(
                user.LocalIdentities[0].Id.Value,
                user.Id.Value,
                passwordHash,
                clock.UtcNow,
                cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result.Failure<ProvisionedOwnerView>(AlreadyProvisioned);
        }

        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain("tenantSlug", tenant.Slug.Value),
            AuditField.Plain("role", TenantRole.TenantOwner.ToString()),
        ]);
        await audit.AppendAsync(
            new AuditRecord(
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
                clock.UtcNow),
            cancellationToken);

        return Result.Success(new ProvisionedOwnerView(
            tenant.Id.Value,
            tenant.Slug.Value,
            tenant.Name,
            user.Id.Value,
            user.Email.Value,
            user.DisplayName,
            TenantRole.TenantOwner.ToString()));
    }

    private static ResultError AlreadyProvisioned => ResultError.Conflict(
        "setup.already_provisioned",
        "The platform already has an owner and cannot be provisioned again.");
}
