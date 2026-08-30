using Vistara.Api.Features.ApiKeys;
using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Auth.ApiKeys;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Bridges API key administration onto the existing issuer, revoker, and
/// tenant-scoped repository so no parallel credential path is introduced.
/// </summary>
internal sealed class PlatformApiKeyAdministrationAdapter(
    ApiKeyIssuer issuer,
    ApiKeyRevoker revoker,
    IApiKeyRepository repository,
    IClock clock) : IApiKeyAdministrationPort
{
    public async ValueTask<IReadOnlyList<ApiKeyView>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ApiKeyMetadata> keys = await repository.ListForTenantAsync(
            new TenantId(tenantId),
            cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        return keys
            .Where(key => key.IsForTenant(new TenantId(tenantId)))
            .OrderBy(key => key.CreatedAt)
            .ThenBy(key => key.Id.Value)
            .Select(key => Describe(key, now))
            .ToArray();
    }

    public async ValueTask<Result<IssuedApiKeyView>> CreateAsync(
        ApiKeyCreateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!TryReadScopes(command.Scopes, out ApiKeyScope scopes))
        {
            return Result.Failure<IssuedApiKeyView>(ResultError.Validation(
                "api_keys.invalid_scopes",
                "One or more requested scopes are not supported."));
        }

        DateTimeOffset now = clock.UtcNow;
        if (command.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            return Result.Failure<IssuedApiKeyView>(ResultError.Validation(
                "api_keys.expiry_in_past",
                "The API key expiry must be in the future."));
        }

        Result<IssuedApiKey> issued = await issuer.IssueAsync(
            new ApiKeyIssueRequest(
                new TenantId(command.TenantId),
                new UserId(command.OwnerId),
                scopes,
                command.ExpiresAt),
            cancellationToken);
        if (!issued.TryGetValue(out IssuedApiKey? key))
        {
            return Result.Failure<IssuedApiKeyView>(issued.Error!);
        }

        return Result.Success(new IssuedApiKeyView(
            new ApiKeyView(
                key.KeyId.Value,
                key.Prefix,
                key.OwnerId.Value,
                DescribeScopes(key.Scopes),
                "Active",
                key.CreatedAt,
                key.ExpiresAt,
                null,
                null),
            key.PlaintextKey));
    }

    public ValueTask<Result> RevokeAsync(
        Guid tenantId,
        Guid actorUserId,
        Guid keyId,
        CancellationToken cancellationToken) =>
        revoker.RevokeAsync(
            new TenantId(tenantId),
            new UserId(actorUserId),
            new ApiKeyId(keyId),
            cancellationToken);

    private static ApiKeyView Describe(ApiKeyMetadata key, DateTimeOffset now) =>
        new(
            key.Id.Value,
            key.Prefix.Value,
            key.OwnerId.Value,
            DescribeScopes(key.Scopes),
            key.GetStatus(now).ToString(),
            key.CreatedAt,
            key.ExpiresAt,
            key.LastUsedAt,
            key.RevokedAt);

    private static string[] DescribeScopes(ApiKeyScope scopes)
    {
        var values = new List<string>(4);
        if (scopes.HasFlag(ApiKeyScope.ReadAssets))
        {
            values.Add(ApiKeyScopeCatalog.ReadAssets);
        }

        if (scopes.HasFlag(ApiKeyScope.UploadAssets))
        {
            values.Add(ApiKeyScopeCatalog.UploadAssets);
        }

        if (scopes.HasFlag(ApiKeyScope.ManageMetadata))
        {
            values.Add(ApiKeyScopeCatalog.ManageMetadata);
        }

        if (scopes.HasFlag(ApiKeyScope.ManageApiKeys))
        {
            values.Add(ApiKeyScopeCatalog.ManageApiKeys);
        }

        values.Sort(StringComparer.Ordinal);
        return [.. values];
    }

    private static bool TryReadScopes(
        IReadOnlyList<string> requested,
        out ApiKeyScope scopes)
    {
        scopes = ApiKeyScope.None;
        foreach (string scope in requested)
        {
            switch (scope)
            {
                case ApiKeyScopeCatalog.ReadAssets:
                    scopes |= ApiKeyScope.ReadAssets;
                    break;
                case ApiKeyScopeCatalog.UploadAssets:
                    scopes |= ApiKeyScope.UploadAssets;
                    break;
                case ApiKeyScopeCatalog.ManageMetadata:
                    scopes |= ApiKeyScope.ManageMetadata;
                    break;
                case ApiKeyScopeCatalog.ManageApiKeys:
                    scopes |= ApiKeyScope.ManageApiKeys;
                    break;
                default:
                    return false;
            }
        }

        return scopes != ApiKeyScope.None;
    }
}
