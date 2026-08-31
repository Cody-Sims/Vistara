using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Vistara.Api.Features.Capabilities;

public enum CapabilitiesAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
}

public sealed record CapabilitiesAccess
{
    private CapabilitiesAccess(CapabilitiesAccessStatus status, Guid tenantId)
    {
        Status = status;
        TenantId = tenantId;
    }

    public CapabilitiesAccessStatus Status { get; }

    public Guid TenantId { get; }

    public static CapabilitiesAccess Authorized(Guid tenantId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        return new(CapabilitiesAccessStatus.Authorized, tenantId);
    }

    public static CapabilitiesAccess Denied(CapabilitiesAccessStatus status)
    {
        if (status == CapabilitiesAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, Guid.Empty);
    }
}

/// <summary>
/// Resolves the tenant whose capability surface the caller may read.
/// </summary>
public interface ICapabilitiesAuthorizationPort
{
    ValueTask<CapabilitiesAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Derives capability access from the platform authentication claims so no
/// parallel tenant resolution path is introduced.
/// </summary>
public sealed class ClaimsCapabilitiesAuthorizationPort : ICapabilitiesAuthorizationPort
{
    internal const string TenantClaimType = "tenant_id";

    public ValueTask<CapabilitiesAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ClaimsPrincipal principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                CapabilitiesAccess.Denied(CapabilitiesAccessStatus.Unauthenticated));
        }

        string[] tenantClaims = principal
            .FindAll(TenantClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tenantClaims.Length != 1 ||
            !Guid.TryParseExact(tenantClaims[0], "D", out Guid tenantId) ||
            tenantId == Guid.Empty ||
            tenantId.Version != 7)
        {
            return ValueTask.FromResult(
                CapabilitiesAccess.Denied(CapabilitiesAccessStatus.Forbidden));
        }

        return ValueTask.FromResult(CapabilitiesAccess.Authorized(tenantId));
    }
}
