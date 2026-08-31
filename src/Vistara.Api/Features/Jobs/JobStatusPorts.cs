using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Vistara.Api.Features.Jobs;

public enum JobAccessStatus
{
    Authorized,
    Unauthenticated,
    Forbidden,
}

public sealed record JobAccess
{
    private JobAccess(JobAccessStatus status, Guid tenantId)
    {
        Status = status;
        TenantId = tenantId;
    }

    public JobAccessStatus Status { get; }

    public Guid TenantId { get; }

    public static JobAccess Authorized(Guid tenantId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);
        return new(JobAccessStatus.Authorized, tenantId);
    }

    public static JobAccess Denied(JobAccessStatus status)
    {
        if (status == JobAccessStatus.Authorized || !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new(status, Guid.Empty);
    }
}

/// <summary>
/// Resolves the tenant whose durable job state the caller may observe.
/// </summary>
public interface IJobStatusAuthorizationPort
{
    ValueTask<JobAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Derives job-status access from the platform authentication claims; reading
/// job state requires the same tenant read scope as reading assets.
/// </summary>
public sealed class ClaimsJobStatusAuthorizationPort : IJobStatusAuthorizationPort
{
    internal const string TenantClaimType = "tenant_id";

    internal const string RequiredScope = "assets.read";

    public ValueTask<JobAccess> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ClaimsPrincipal principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return ValueTask.FromResult(
                JobAccess.Denied(JobAccessStatus.Unauthenticated));
        }

        string[] tenants = principal
            .FindAll(TenantClaimType)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        if (tenants.Length != 1 ||
            !Guid.TryParse(tenants[0], out Guid tenantId) ||
            tenantId == Guid.Empty ||
            tenantId.Version != 7 ||
            !principal.HasClaim("scope", RequiredScope))
        {
            return ValueTask.FromResult(JobAccess.Denied(JobAccessStatus.Forbidden));
        }

        return ValueTask.FromResult(JobAccess.Authorized(tenantId));
    }
}
