using Vistara.Domain.Common;

namespace Vistara.Api.Features.Account;

public sealed record CurrentUserTenantView(
    Guid TenantId,
    string Slug,
    string Name,
    string Role,
    string MembershipStatus);

public sealed record CurrentUserView(
    Guid UserId,
    string Email,
    string DisplayName,
    Guid? TenantId,
    string? Role,
    IReadOnlyList<CurrentUserTenantView> Tenants);

public sealed record BrowserLoginCommand(
    string Login,
    string Password,
    Guid? TenantId,
    string? ExistingSessionToken);

public sealed record BrowserSessionResult(
    CurrentUserView User,
    string SetCookieHeader,
    string AntiforgeryToken);

/// <summary>
/// Browser session lifecycle backed by the existing cookie session manager,
/// local credential verifier, and tenancy repositories.
/// </summary>
public interface IBrowserSessionPort
{
    ValueTask<Result<BrowserSessionResult>> LoginAsync(
        BrowserLoginCommand command,
        CancellationToken cancellationToken);

    /// <summary>
    /// Issues a fresh antiforgery token for a live cookie session so a
    /// reloaded browser can make unsafe requests without signing in again.
    /// Returns <c>null</c> when the caller holds no live browser session.
    /// </summary>
    ValueTask<string?> IssueAntiforgeryTokenAsync(
        string? sessionToken,
        CancellationToken cancellationToken);

    /// <summary>Revokes the session and returns the deletion cookie header.</summary>
    ValueTask<string> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken);

    /// <summary>
    /// Describes the current principal. <paramref name="includeOtherTenants"/>
    /// must be false for tenant-bound credentials so an API key cannot
    /// enumerate its owner's other tenants.
    /// </summary>
    ValueTask<Result<CurrentUserView>> DescribeAsync(
        Guid tenantId,
        Guid userId,
        bool includeOtherTenants,
        CancellationToken cancellationToken);
}

public sealed record FirstOwnerProvisioningCommand(
    string TenantSlug,
    string TenantName,
    string Email,
    string DisplayName,
    string Password);

public sealed record ProvisionedOwnerView(
    Guid TenantId,
    string TenantSlug,
    string TenantName,
    Guid UserId,
    string Email,
    string DisplayName,
    string Role);

/// <summary>
/// One-time first-owner provisioning. Implementations must refuse to run once
/// any identity exists so the bootstrap route cannot be replayed.
/// </summary>
public interface IFirstOwnerProvisioningPort
{
    /// <summary>
    /// Reports whether provisioning is still open, so a first-run client can
    /// offer the setup route without attempting it.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
        FirstOwnerProvisioningCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs inside the first-owner provisioning transaction immediately before it
/// commits. The default implementation does nothing; a host may substitute one
/// to assert or exercise rollback behaviour.
/// </summary>
public interface IFirstOwnerProvisioningGuard
{
    ValueTask BeforeCommitAsync(CancellationToken cancellationToken);
}

public sealed class NoOpFirstOwnerProvisioningGuard : IFirstOwnerProvisioningGuard
{
    public ValueTask BeforeCommitAsync(CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
