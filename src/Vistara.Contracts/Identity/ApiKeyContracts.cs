using System.Text.Json.Serialization;

namespace Vistara.Contracts.Identity;

public sealed record ApiKeySummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("prefix")] string Prefix,
    [property: JsonPropertyName("ownerId")] Guid OwnerId,
    [property: JsonPropertyName("scopes")] IReadOnlyList<string> Scopes,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt,
    [property: JsonPropertyName("revokedAt")] DateTimeOffset? RevokedAt);

public sealed record ApiKeyCollectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<ApiKeySummaryResponse> Items);

public sealed record CreateApiKeyRequest(
    [property: JsonPropertyName("scopes")] IReadOnlyList<string>? Scopes,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);

/// <summary>
/// The single response that carries the plaintext secret. The secret is never
/// stored and is never returned again.
/// </summary>
public sealed record CreatedApiKeyResponse(
    [property: JsonPropertyName("key")] ApiKeySummaryResponse Key,
    [property: JsonPropertyName("secret")] string Secret);
