using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Identity;

public interface IApiKeyRepository
{
    ValueTask<ApiKeyMetadata?> FindByIdAsync(
        TenantId tenantId,
        ApiKeyId id,
        CancellationToken cancellationToken);

    ValueTask<ApiKeyMetadata?> FindByPrefixAsync(
        ApiKeyPrefix prefix,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<ApiKeyMetadata>> ListForTenantAsync(
        TenantId tenantId,
        CancellationToken cancellationToken);

    ValueTask AddAsync(ApiKeyMetadata apiKey, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        ApiKeyMetadata apiKey,
        long expectedVersion,
        CancellationToken cancellationToken);
}
