using System.Globalization;
using Microsoft.Extensions.Options;
using Vistara.Api.Features.Admin;
using Vistara.Api.Features.Oidc;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Identity;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// Hosted OpenID Connect configuration, bound from
/// <c>Platform:Authentication:Oidc</c>.
///
/// Hosted sign-in is disabled unless an operator turns it on and configures a
/// provider, so a Compose deployment keeps local password sign-in and nothing
/// else. Every value here is validated at startup: a deployment that would
/// accept the wrong directory, an unregistered reply URL, or no client
/// credential must fail to start rather than fail at the first sign-in.
/// </summary>
public sealed class PlatformOidcOptions
{
    public const string SectionName = "Platform:Authentication:Oidc";

    public bool Enabled { get; set; }

    public List<PlatformOidcProviderOptions> Providers { get; set; } = [];
}

public sealed class PlatformOidcProviderOptions
{
    /// <summary>The provider key, which is also the route segment.</summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// The label a first-run client renders on the sign-in button. It is the
    /// only provider string published to a browser, so it is required and
    /// bounded rather than defaulted from the key.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>The one Entra directory tenant (<c>tid</c>) accepted.</summary>
    public string? TenantId { get; set; }

    /// <summary>The Entra application (client) identifier.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The registered reply URL. It must equal the Entra registration byte for
    /// byte, so its path must be the frozen callback route.
    /// </summary>
    public string? RedirectUri { get; set; }

    public string? Authority { get; set; }

    public string? ApplicationBaseUri { get; set; }

    public string? PostLogoutRedirectUri { get; set; }

    public List<string> Scopes { get; set; } = [];

    public List<string> AllowedSigningAlgorithms { get; set; } = [];

    public int? ClockSkewSeconds { get; set; }

    public int? HttpTimeoutSeconds { get; set; }

    public int? MetadataCacheLifetimeSeconds { get; set; }

    public int? MetadataRefreshBackoffSeconds { get; set; }

    public int? MetadataStaleWhileUnavailableSeconds { get; set; }

    public int? LoginRequestLifetimeSeconds { get; set; }

    /// <summary>
    /// Loopback HTTP is only ever allowed for an integration fixture, so this
    /// stays true in every deployment.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// The user-assigned managed identity that mints the federated client
    /// assertion. This is the secretless path and the intended production
    /// configuration.
    /// </summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// The explicit fallback for a deployment that cannot federate a managed
    /// identity. It is never printed, and the value is only ever revealed to
    /// build one token request.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>The client secret must never reach a log or an error message.</summary>
    public override string ToString() =>
        $"{nameof(PlatformOidcProviderOptions)} {{ ProviderId = {ProviderId}, ClientSecret = {RedactedSecret.Placeholder} }}";
}

/// <summary>
/// Bootstrap configuration bound from <c>Platform:Bootstrap</c>. The first
/// owner allowlist is the only thing that can turn a hosted sign-in into an
/// owner, and it is deliberately expressed as exact directory identifiers.
/// </summary>
public sealed class PlatformBootstrapOptions
{
    public const string SectionName = "Platform:Bootstrap";

    public PlatformFirstOwnerOptions FirstOwner { get; set; } = new();
}

public sealed class PlatformFirstOwnerOptions
{
    /// <summary>
    /// Whether an allowlisted external identity may claim the bootstrap
    /// singleton. Off by default: local first-owner setup is unaffected.
    /// </summary>
    public bool Enabled { get; set; }

    public string? ProviderId { get; set; }

    /// <summary>The one directory tenant the allowlisted objects belong to.</summary>
    public string? DirectoryTenantId { get; set; }

    /// <summary>
    /// Exact Entra object identifiers (<c>oid</c>). An email address, a domain,
    /// or a display name is deliberately not accepted here: a mailbox can be
    /// renamed or reassigned and must never grant ownership.
    /// </summary>
    public List<string> AllowedObjectIds { get; set; } = [];

    public string? TenantSlug { get; set; }

    public string? TenantName { get; set; }
}

/// <summary>
/// The validated first-owner allowlist. Construction is the only way to obtain
/// one, so an unvalidated allowlist cannot reach the sign-in path.
/// </summary>
public sealed class PlatformFirstOwnerPolicy
{
    private readonly HashSet<Guid> _allowedObjectIds;

    internal PlatformFirstOwnerPolicy(
        string providerId,
        Guid directoryTenantId,
        IReadOnlyCollection<Guid> allowedObjectIds,
        string tenantSlug,
        string tenantName)
    {
        ProviderId = providerId;
        DirectoryTenantId = directoryTenantId;
        _allowedObjectIds = [.. allowedObjectIds];
        TenantSlug = tenantSlug;
        TenantName = tenantName;
    }

    public static PlatformFirstOwnerPolicy Disabled { get; } = new();

    private PlatformFirstOwnerPolicy()
    {
        ProviderId = string.Empty;
        DirectoryTenantId = Guid.Empty;
        _allowedObjectIds = [];
        TenantSlug = string.Empty;
        TenantName = string.Empty;
        IsEnabled = false;
    }

    public bool IsEnabled { get; private init; } = true;

    public string ProviderId { get; }

    public Guid DirectoryTenantId { get; }

    public string TenantSlug { get; }

    public string TenantName { get; }

    public int AllowedObjectIdCount => _allowedObjectIds.Count;

    /// <summary>
    /// Reports whether one directory identity may claim the bootstrap
    /// singleton. Provider, directory tenant, and object identifier must all
    /// match exactly; nothing else participates.
    /// </summary>
    public bool Allows(string providerId, Guid directoryTenantId, Guid objectId) =>
        IsEnabled &&
        string.Equals(providerId, ProviderId, StringComparison.Ordinal) &&
        directoryTenantId != Guid.Empty &&
        directoryTenantId == DirectoryTenantId &&
        objectId != Guid.Empty &&
        _allowedObjectIds.Contains(objectId);
}

/// <summary>
/// Turns configuration into the validated provider and bootstrap types. Every
/// failure throws, and no message ever contains a client secret.
/// </summary>
public static class PlatformOidcConfiguration
{
    /// <summary>
    /// The margin the shared <see cref="HttpClient"/> timeout keeps above the
    /// per-provider budget. The provider budget must be the one that expires,
    /// because it is the only one that can be attributed to the provider; a
    /// client timeout would surface as a transport fault instead.
    /// </summary>
    public static readonly TimeSpan HttpClientTimeoutMargin = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<PlatformOidcProviderRegistration> CreateProviders(
        PlatformOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var registrations = new List<PlatformOidcProviderRegistration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlatformOidcProviderOptions configured in options.Providers)
        {
            PlatformOidcProviderRegistration registration = CreateProvider(configured);
            if (!seen.Add(registration.ProviderId))
            {
                throw new ArgumentException(
                    $"The OIDC provider '{registration.ProviderId}' is configured more than once.",
                    nameof(options));
            }

            registrations.Add(registration);
        }

        if (options.Enabled && registrations.Count == 0)
        {
            throw new ArgumentException(
                "Hosted OIDC sign-in is enabled but no provider is configured.",
                nameof(options));
        }

        // Every configured provider is validated whether or not the feature is
        // switched on, so an operator cannot park a broken provider behind
        // `Enabled: false` and discover it only on the day it is turned on. The
        // switch decides what is composed, not what is checked.
        return options.Enabled ? registrations : [];
    }

    /// <summary>
    /// The timeout for the shared OIDC <see cref="HttpClient"/>: strictly
    /// greater than every configured provider budget.
    /// </summary>
    public static TimeSpan CreateHttpClientTimeout(PlatformOidcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan longest = OidcProviderOptions.MaximumHttpTimeout;
        foreach (PlatformOidcProviderOptions configured in options.Providers)
        {
            if (configured.HttpTimeoutSeconds is { } seconds && seconds > 0)
            {
                TimeSpan candidate = TimeSpan.FromSeconds(seconds);
                longest = candidate > longest ? candidate : longest;
            }
        }

        return longest + HttpClientTimeoutMargin;
    }

    public static PlatformFirstOwnerPolicy CreateFirstOwnerPolicy(
        PlatformBootstrapOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlatformFirstOwnerOptions configured = options.FirstOwner ??
            throw new ArgumentException(
                "The first-owner bootstrap configuration is required.",
                nameof(options));
        if (!configured.Enabled)
        {
            return PlatformFirstOwnerPolicy.Disabled;
        }

        if (!ExternalFirstOwnerProviders.TryNormalize(
                configured.ProviderId,
                out string providerId))
        {
            throw new ArgumentException(
                $"'{configured.ProviderId}' is not a supported external identity provider.",
                nameof(options));
        }

        Guid directoryTenantId = ParseRequiredGuid(
            configured.DirectoryTenantId,
            $"{PlatformBootstrapOptions.SectionName}:FirstOwner:DirectoryTenantId");
        var allowed = new HashSet<Guid>();
        foreach (string candidate in configured.AllowedObjectIds)
        {
            Guid objectId = ParseRequiredGuid(
                candidate,
                $"{PlatformBootstrapOptions.SectionName}:FirstOwner:AllowedObjectIds");
            if (!allowed.Add(objectId))
            {
                throw new ArgumentException(
                    "The first-owner object identifier allowlist contains a duplicate entry.",
                    nameof(options));
            }
        }

        if (allowed.Count == 0)
        {
            throw new ArgumentException(
                "External first-owner bootstrap is enabled but the object identifier allowlist is empty.",
                nameof(options));
        }

        Result<TenantSlug> slug = TenantSlug.Create(configured.TenantSlug ?? string.Empty);
        if (!slug.TryGetValue(out TenantSlug tenantSlug))
        {
            throw new ArgumentException(
                "The bootstrap tenant slug is not a valid tenant slug.",
                nameof(options));
        }

        string tenantName = configured.TenantName?.Trim() ?? string.Empty;
        if (tenantName.Length is 0 or > 128)
        {
            throw new ArgumentException(
                "The bootstrap tenant name is required and must be at most 128 characters.",
                nameof(options));
        }

        return new PlatformFirstOwnerPolicy(
            providerId,
            directoryTenantId,
            allowed,
            tenantSlug.Value,
            tenantName);
    }

    private static PlatformOidcProviderRegistration CreateProvider(
        PlatformOidcProviderOptions configured)
    {
        ArgumentNullException.ThrowIfNull(configured);
        if (!ExternalFirstOwnerProviders.TryNormalize(
                configured.ProviderId,
                out string providerId) ||
            !OidcRoutes.IsProviderKey(providerId))
        {
            throw new ArgumentException(
                $"'{configured.ProviderId}' is not a supported OIDC provider key.",
                nameof(configured));
        }

        Guid tenantId = ParseRequiredGuid(
            configured.TenantId,
            $"{PlatformOidcOptions.SectionName}:Providers:TenantId");
        string displayName = ValidateDisplayName(configured.DisplayName);
        Uri redirectUri = ParseAbsoluteUri(
            configured.RedirectUri,
            $"{PlatformOidcOptions.SectionName}:Providers:RedirectUri");

        // The reply URL is registered with the provider, so a configured value
        // whose path is not the frozen callback route can never receive a
        // callback. Catching it here fails the deployment instead of every
        // sign-in.
        if (!string.Equals(
                redirectUri.AbsolutePath,
                OidcRoutes.CallbackPath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The redirect URI path must be '{OidcRoutes.CallbackPath}'.",
                nameof(configured));
        }

        Uri? postLogoutRedirectUri = configured.PostLogoutRedirectUri is null
            ? null
            : ParseAbsoluteUri(
                configured.PostLogoutRedirectUri,
                $"{PlatformOidcOptions.SectionName}:Providers:PostLogoutRedirectUri");
        if (postLogoutRedirectUri is not null &&
            !string.Equals(
                postLogoutRedirectUri.AbsolutePath,
                OidcRoutes.SignedOutPath,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The post-logout redirect URI path must be '{OidcRoutes.SignedOutPath}'.",
                nameof(configured));
        }

        var options = new OidcProviderOptions(
            tenantId,
            configured.ClientId ?? string.Empty,
            redirectUri,
            configured.Authority is null
                ? null
                : ParseAbsoluteUri(
                    configured.Authority,
                    $"{PlatformOidcOptions.SectionName}:Providers:Authority"),
            configured.ApplicationBaseUri is null
                ? null
                : ParseAbsoluteUri(
                    configured.ApplicationBaseUri,
                    $"{PlatformOidcOptions.SectionName}:Providers:ApplicationBaseUri"),
            postLogoutRedirectUri,
            configured.Scopes.Count == 0 ? null : [.. configured.Scopes],
            configured.AllowedSigningAlgorithms.Count == 0
                ? null
                : [.. configured.AllowedSigningAlgorithms],
            FromSeconds(configured.ClockSkewSeconds),
            FromSeconds(configured.HttpTimeoutSeconds),
            FromSeconds(configured.MetadataCacheLifetimeSeconds),
            FromSeconds(configured.MetadataRefreshBackoffSeconds),
            FromSeconds(configured.MetadataStaleWhileUnavailableSeconds),
            FromSeconds(configured.LoginRequestLifetimeSeconds),
            configured.RequireHttps);

        string? managedIdentityClientId = Trim(configured.ManagedIdentityClientId);
        if (managedIdentityClientId is not null &&
            (!Guid.TryParseExact(managedIdentityClientId, "D", out Guid managedIdentity) ||
                managedIdentity == Guid.Empty))
        {
            throw new ArgumentException(
                "The OIDC managed identity client identifier must be a GUID.",
                nameof(configured));
        }

        RedactedSecret? clientSecret = RedactedSecret.From(configured.ClientSecret?.Trim());
        if (managedIdentityClientId is null && clientSecret is null)
        {
            throw new ArgumentException(
                "An OIDC provider requires a managed identity client identifier or an explicit client secret.",
                nameof(configured));
        }

        return new PlatformOidcProviderRegistration(
            providerId,
            displayName,
            options,
            managedIdentityClientId,
            clientSecret);
    }

    /// <summary>
    /// The display name is the one provider string that reaches a browser, so
    /// it is required, bounded, and restricted to printable characters that
    /// carry no markup or control meaning.
    /// </summary>
    private static string ValidateDisplayName(string? displayName)
    {
        string value = displayName?.Trim() ?? string.Empty;
        if (value.Length is 0 or > 64 ||
            value.Any(character =>
                char.IsControl(character) || character is '<' or '>' or '&' or '"'))
        {
            throw new ArgumentException(
                "An OIDC provider display name is required and must be at most 64 printable characters.",
                nameof(displayName));
        }

        return value;
    }

    private static TimeSpan? FromSeconds(int? seconds) =>
        seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Guid ParseRequiredGuid(string? value, string name)
    {
        if (!Guid.TryParseExact(value?.Trim(), "D", out Guid parsed) ||
            parsed == Guid.Empty)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' must be a non-empty GUID."),
                nameof(value));
        }

        return parsed;
    }

    private static Uri ParseAbsoluteUri(string? value, string name)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? parsed))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' must be an absolute URL."),
                nameof(value));
        }

        return parsed;
    }
}

/// <summary>
/// One validated provider: the protocol options plus the credential source the
/// token exchange must use.
/// </summary>
public sealed class PlatformOidcProviderRegistration
{
    internal PlatformOidcProviderRegistration(
        string providerId,
        string displayName,
        OidcProviderOptions options,
        string? managedIdentityClientId,
        RedactedSecret? clientSecret)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        Options = options;
        ManagedIdentityClientId = managedIdentityClientId;
        ClientSecret = clientSecret;
    }

    public string ProviderId { get; }

    /// <summary>The label a first-run client renders for this provider.</summary>
    public string DisplayName { get; }

    public OidcProviderOptions Options { get; }

    /// <summary>Null when the deployment uses the explicit secret fallback.</summary>
    public string? ManagedIdentityClientId { get; }

    /// <summary>Null when the deployment uses the secretless managed identity.</summary>
    public RedactedSecret? ClientSecret { get; }

    public override string ToString() =>
        $"{nameof(PlatformOidcProviderRegistration)} {{ ProviderId = {ProviderId} }}";
}

internal sealed class PlatformOidcOptionsValidator : IValidateOptions<PlatformOidcOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformOidcOptions options)
    {
        try
        {
            _ = PlatformOidcConfiguration.CreateProviders(options);
        }
        catch (Exception error) when (
            error is ArgumentException or FormatException or UriFormatException)
        {
            return ValidateOptionsResult.Fail(
                $"{PlatformOidcOptions.SectionName} is invalid: {error.Message}");
        }

        return ValidateOptionsResult.Success;
    }
}

internal sealed class PlatformBootstrapOptionsValidator :
    IValidateOptions<PlatformBootstrapOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformBootstrapOptions options)
    {
        try
        {
            _ = PlatformOidcConfiguration.CreateFirstOwnerPolicy(options);
        }
        catch (Exception error) when (
            error is ArgumentException or FormatException)
        {
            return ValidateOptionsResult.Fail(
                $"{PlatformBootstrapOptions.SectionName} is invalid: {error.Message}");
        }

        return ValidateOptionsResult.Success;
    }
}
