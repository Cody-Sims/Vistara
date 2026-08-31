using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Vistara.Domain.Tenancy;

namespace Vistara.Api.Features.Account;

public enum AccountOperation
{
    ReadSelf,
    ReadTenants,
    ReadMembers,
    ManageMembers,
    ReadApiKeys,
    ManageApiKeys,
}

public enum AccountAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
}

public enum AccountAuthenticationKind
{
    /// <summary>An automation or federated credential bound to one tenant.</summary>
    TenantBound,

    /// <summary>An interactive browser session owned by a human principal.</summary>
    Browser,
}

public sealed record AccountActor(
    Guid TenantId,
    Guid UserId,
    TenantRole Role,
    AccountAuthenticationKind AuthenticationKind)
{
    /// <summary>
    /// Only an interactive browser session may enumerate the principal's other
    /// tenants. A tenant-bound credential such as an API key must never reveal
    /// where its owner is a member outside the tenant it was issued for.
    /// </summary>
    public bool MayEnumerateOtherTenants =>
        AuthenticationKind == AccountAuthenticationKind.Browser;
}

public sealed record AccountAccess
{
    private AccountAccess(AccountAccessStatus status, AccountActor? actor)
    {
        Status = status;
        Actor = actor;
    }

    public AccountAccessStatus Status { get; }

    public AccountActor? Actor { get; }

    public static AccountAccess Authorized(AccountActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(AccountAccessStatus.Authorized, actor);
    }

    public static AccountAccess Denied(AccountAccessStatus status)
    {
        if (status == AccountAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, null);
    }
}

/// <summary>
/// Resolves the tenant-scoped actor permitted to perform an account or
/// platform administration operation.
/// </summary>
public interface IAccountAuthorizationPort
{
    ValueTask<AccountAccess> AuthorizeAsync(
        HttpContext context,
        AccountOperation operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Derives account access from the platform authentication claims that the
/// cookie, API key, and bearer handlers already publish.
/// </summary>
public sealed class ClaimsAccountAuthorizationPort : IAccountAuthorizationPort
{
    internal const string TenantClaimType = "tenant_id";

    internal const string ScopeClaimType = "scope";

    internal const string AuthenticationKindClaimType = "vistara_auth_kind";

    internal const string BrowserAuthenticationKind = "Cookie";

    public ValueTask<AccountAccess> AuthorizeAsync(
        HttpContext context,
        AccountOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        cancellationToken.ThrowIfCancellationRequested();

        ClaimsPrincipal principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                AccountAccess.Denied(AccountAccessStatus.Unauthenticated));
        }

        if (!TryReadUuid7(principal, TenantClaimType, out Guid tenantId) ||
            !TryReadUuid7(principal, ClaimTypes.NameIdentifier, out Guid userId) ||
            !TryReadRole(principal, out TenantRole role))
        {
            return ValueTask.FromResult(
                AccountAccess.Denied(AccountAccessStatus.Forbidden));
        }

        string? requiredScope = RequiredScope(operation);
        if (requiredScope is not null &&
            !principal.HasClaim(ScopeClaimType, requiredScope))
        {
            return ValueTask.FromResult(
                AccountAccess.Denied(AccountAccessStatus.Forbidden));
        }

        if (!HasMinimumRole(role, MinimumRole(operation)))
        {
            return ValueTask.FromResult(
                AccountAccess.Denied(AccountAccessStatus.Forbidden));
        }

        return ValueTask.FromResult(
            AccountAccess.Authorized(new AccountActor(
                tenantId,
                userId,
                role,
                ReadAuthenticationKind(principal))));
    }

    internal static string? RequiredScope(AccountOperation operation) =>
        operation switch
        {
            AccountOperation.ReadSelf or AccountOperation.ReadTenants => null,
            AccountOperation.ReadMembers or
            AccountOperation.ManageMembers => "members.manage",
            AccountOperation.ReadApiKeys or
            AccountOperation.ManageApiKeys => "api_keys.manage",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    internal static TenantRole MinimumRole(AccountOperation operation) =>
        operation switch
        {
            AccountOperation.ReadSelf or AccountOperation.ReadTenants =>
                TenantRole.Viewer,
            AccountOperation.ReadMembers or
            AccountOperation.ManageMembers or
            AccountOperation.ReadApiKeys or
            AccountOperation.ManageApiKeys => TenantRole.TenantAdmin,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    internal static AccountAuthenticationKind ReadAuthenticationKind(
        ClaimsPrincipal principal)
    {
        string[] kinds = principal.FindAll(AuthenticationKindClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return kinds.Length == 1 &&
            string.Equals(
                kinds[0],
                BrowserAuthenticationKind,
                StringComparison.Ordinal)
            ? AccountAuthenticationKind.Browser
            : AccountAuthenticationKind.TenantBound;
    }

    private static bool HasMinimumRole(TenantRole actual, TenantRole minimum) =>
        Rank(actual) >= Rank(minimum);

    private static int Rank(TenantRole role) =>
        role switch
        {
            TenantRole.Viewer => 0,
            TenantRole.Member => 1,
            TenantRole.TenantAdmin => 2,
            TenantRole.TenantOwner => 3,
            _ => -1,
        };

    private static bool TryReadRole(ClaimsPrincipal principal, out TenantRole role)
    {
        string[] values = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (values.Length != 1)
        {
            role = default;
            return false;
        }

        return Enum.TryParse(values[0], ignoreCase: false, out role) &&
            Enum.IsDefined(role);
    }

    private static bool TryReadUuid7(
        ClaimsPrincipal principal,
        string claimType,
        out Guid value)
    {
        value = default;
        string[] values = principal.FindAll(claimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        return values.Length == 1 &&
            Guid.TryParse(values[0], out value) &&
            value != Guid.Empty &&
            value.Version == 7;
    }
}
