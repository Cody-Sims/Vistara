using System.Text.Json.Serialization;

namespace Vistara.Contracts.Identity;

public sealed record LoginRequest(
    [property: JsonPropertyName("login")] string? Login,
    [property: JsonPropertyName("password")] string? Password,
    [property: JsonPropertyName("tenantId")] Guid? TenantId);

public sealed record CurrentUserTenantResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("membershipStatus")] string MembershipStatus);

public sealed record CurrentUserResponse(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("tenantId")] Guid? TenantId,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("tenants")] IReadOnlyList<CurrentUserTenantResponse> Tenants,
    [property: JsonPropertyName("csrfHeaderName")] string CsrfHeaderName,
    [property: JsonPropertyName("csrfToken")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CsrfToken = null);

public sealed record LoginResponse(
    [property: JsonPropertyName("user")] CurrentUserResponse User,
    [property: JsonPropertyName("csrfToken")] string CsrfToken);

/// <summary>
/// Whether first-owner provisioning is still open. Answered anonymously so a
/// first-run client can link to setup without guessing.
/// </summary>
public sealed record SetupAvailabilityResponse(
    [property: JsonPropertyName("available")] bool Available);

public sealed record ProvisionFirstOwnerRequest(
    [property: JsonPropertyName("tenantSlug")] string? TenantSlug,
    [property: JsonPropertyName("tenantName")] string? TenantName,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("password")] string? Password);

public sealed record ProvisionFirstOwnerResponse(
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("tenantSlug")] string TenantSlug,
    [property: JsonPropertyName("tenantName")] string TenantName,
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("role")] string Role);

public sealed record UserPreferencesResponse(
    [property: JsonPropertyName("density")] string Density,
    [property: JsonPropertyName("reducedMotion")] bool ReducedMotion,
    [property: JsonPropertyName("screenReaderPagedMode")] bool ScreenReaderPagedMode,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("version")] long Version);
