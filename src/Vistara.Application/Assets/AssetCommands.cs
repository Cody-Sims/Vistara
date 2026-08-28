using Vistara.Domain.Assets;
using Vistara.Domain.Common;

namespace Vistara.Application.Assets;

public sealed record UpdateAssetMetadataCommand(
    Guid TenantId,
    Guid AssetId,
    string Title,
    string? Description,
    AssetVisibility Visibility,
    long ExpectedVersion,
    DateTimeOffset ChangedAtUtc);

public sealed record AssetMutationResult(
    Guid TenantId,
    Guid AssetId,
    long Version,
    DateTimeOffset UpdatedAtUtc);

public interface IAssetMetadataCommandHandler
{
    ValueTask<Result<AssetMutationResult>> HandleAsync(
        UpdateAssetMetadataCommand command,
        CancellationToken cancellationToken);
}
