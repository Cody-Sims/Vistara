using Vistara.Domain.Common;

namespace Vistara.Api.Features.ApiKeys;

public sealed record ApiKeyView(
    Guid Id,
    string Prefix,
    Guid OwnerId,
    IReadOnlyList<string> Scopes,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record IssuedApiKeyView(ApiKeyView Key, string Secret);

public sealed record ApiKeyCreateCommand(
    Guid TenantId,
    Guid OwnerId,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Tenant-scoped API key administration backed by the existing issuer,
/// revoker, and repository rather than a parallel credential store.
/// </summary>
public interface IApiKeyAdministrationPort
{
    ValueTask<IReadOnlyList<ApiKeyView>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    ValueTask<Result<IssuedApiKeyView>> CreateAsync(
        ApiKeyCreateCommand command,
        CancellationToken cancellationToken);

    ValueTask<Result> RevokeAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid keyId,
        CancellationToken cancellationToken);
}

public static class ApiKeyScopeCatalog
{
    public const string ReadAssets = "assets.read";

    public const string UploadAssets = "assets.upload";

    public const string ManageMetadata = "metadata.manage";

    public const string ManageApiKeys = "api_keys.manage";

    public static IReadOnlyList<string> All { get; } =
    [
        ReadAssets,
        UploadAssets,
        ManageMetadata,
        ManageApiKeys,
    ];

    public static bool IsKnown(string scope) =>
        All.Contains(scope, StringComparer.Ordinal);
}
