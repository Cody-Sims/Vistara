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

    /// <summary>Revokes the session and returns the deletion cookie header.</summary>
    ValueTask<string> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken);

    ValueTask<Result<CurrentUserView>> DescribeAsync(
        Guid tenantId,
        Guid userId,
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
    ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
        FirstOwnerProvisioningCommand command,
        CancellationToken cancellationToken);
}
