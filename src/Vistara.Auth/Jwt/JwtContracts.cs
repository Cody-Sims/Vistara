using Microsoft.IdentityModel.Tokens;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Jwt;

public sealed record JwtTenantMembership(
    UserId UserId,
    TenantId TenantId,
    TenantStatus TenantStatus,
    MembershipStatus MembershipStatus,
    TenantRole Role);

public sealed record JwtPrincipal(
    UserId UserId,
    TenantId TenantId,
    TenantRole Role,
    string Issuer,
    string Subject,
    string JwtId);

public interface IJwtTenantMembershipProvider
{
    ValueTask<JwtTenantMembership?> FindAsync(
        string issuer,
        string subject,
        TenantId tenantId,
        CancellationToken cancellationToken);
}

public interface IJwtRevocationStore
{
    ValueTask<bool> IsRevokedAsync(
        string issuer,
        string jwtId,
        CancellationToken cancellationToken);
}

public interface IJwtMetadataSigningKeyResolver
{
    ValueTask<IReadOnlyCollection<SecurityKey>> ResolveAsync(
        Uri metadataAddress,
        CancellationToken cancellationToken);
}
