using System.Text.Json.Serialization;

namespace Vistara.Contracts.Identity;

public sealed record TenantSummaryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("membershipStatus")] string MembershipStatus,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt);

public sealed record TenantCollectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TenantSummaryResponse> Items);

public sealed record TenantMemberResponse(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("invitedAt")] DateTimeOffset InvitedAt,
    [property: JsonPropertyName("joinedAt")] DateTimeOffset? JoinedAt,
    [property: JsonPropertyName("version")] long Version);

public sealed record TenantMemberCollectionResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TenantMemberResponse> Items);

public sealed record InviteTenantMemberRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role);
