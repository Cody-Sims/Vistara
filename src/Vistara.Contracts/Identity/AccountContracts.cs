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
    [property: JsonPropertyName("authenticationKind")] string AuthenticationKind,
    [property: JsonPropertyName("csrfHeaderName")] string CsrfHeaderName,
    [property: JsonPropertyName("csrfToken")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CsrfToken = null);

/// <summary>
/// The credential kind that authenticated a response. The vocabulary is closed
/// and stable: a client selects behaviour from this value and must never infer
/// the credential from the presence of an antiforgery token. Only
/// <see cref="Cookie"/> carries <c>csrfToken</c>.
/// </summary>
public static class AuthenticationKinds
{
    /// <summary>An interactive browser session cookie.</summary>
    public const string Cookie = "cookie";

    /// <summary>A tenant-bound API key presented in the API key header.</summary>
    public const string ApiKey = "apiKey";

    /// <summary>A federated bearer token presented in the authorization header.</summary>
    public const string Bearer = "bearer";

    public static IReadOnlyList<string> All { get; } = [Cookie, ApiKey, Bearer];
}

public sealed record LoginResponse(
    [property: JsonPropertyName("user")] CurrentUserResponse User,
    [property: JsonPropertyName("csrfToken")] string CsrfToken);

/// <summary>
/// Whether first-owner provisioning is still open, and which hosted identity
/// providers a visitor may sign in with. Answered anonymously so a first-run
/// client can link to setup and render sign-in without guessing.
/// </summary>
public sealed record SetupAvailabilityResponse(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("signInProviders")]
    IReadOnlyList<SignInProviderDescriptor> SignInProviders);

/// <summary>
/// One hosted sign-in provider. Only the key, the label, and the URL to start
/// sign-in at are published: directory tenants, client identifiers,
/// authorities, and bootstrap allowlists never leave the server. The start URL
/// is a same-origin path, and starting sign-in is a browser navigation rather
/// than a fetch.
/// </summary>
public sealed record SignInProviderDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("startUrl")] string StartUrl);

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
