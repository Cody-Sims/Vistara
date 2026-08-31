using Microsoft.IdentityModel.Tokens;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Validated configuration for one Microsoft Entra ID OpenID Connect provider.
/// The type fails closed at construction so a misconfigured deployment cannot
/// start a sign-in flow that would accept the wrong directory, a redirect the
/// operator never approved, or a token signed with a downgraded algorithm.
/// </summary>
public sealed class OidcProviderOptions
{
    public const string EntraLoginHost = "login.microsoftonline.com";
    public const string MetadataPath = ".well-known/openid-configuration";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumHttpTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumMetadataCacheLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MinimumMetadataCacheLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumLoginRequestLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The fixed Entra tenant that holds personal Microsoft accounts. Vistara
    /// binds hosted sign-in to one organizational directory, so this tenant is
    /// never a valid configuration value.
    /// </summary>
    private static readonly Guid PersonalAccountTenantId =
        Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad");

    private static readonly HashSet<string> MultiTenantAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "common",
            "organizations",
            "consumers",
        };

    private static readonly HashSet<string> SupportedSigningAlgorithms =
        new(StringComparer.Ordinal)
        {
            SecurityAlgorithms.RsaSha256,
            SecurityAlgorithms.RsaSha384,
            SecurityAlgorithms.RsaSha512,
            SecurityAlgorithms.RsaSsaPssSha256,
            SecurityAlgorithms.RsaSsaPssSha384,
            SecurityAlgorithms.RsaSsaPssSha512,
            SecurityAlgorithms.EcdsaSha256,
            SecurityAlgorithms.EcdsaSha384,
            SecurityAlgorithms.EcdsaSha512,
        };

    private static readonly string[] DefaultScopes = ["openid", "profile", "email"];

    public OidcProviderOptions(
        Guid tenantId,
        string clientId,
        Uri redirectUri,
        Uri? authority = null,
        Uri? applicationBaseUri = null,
        Uri? postLogoutRedirectUri = null,
        IReadOnlyCollection<string>? scopes = null,
        IReadOnlyCollection<string>? allowedSigningAlgorithms = null,
        TimeSpan? clockSkew = null,
        TimeSpan? httpTimeout = null,
        TimeSpan? metadataCacheLifetime = null,
        TimeSpan? metadataRefreshBackoff = null,
        TimeSpan? loginRequestLifetime = null,
        bool requireHttps = true)
    {
        if (tenantId == Guid.Empty || tenantId == PersonalAccountTenantId)
        {
            throw new ArgumentException(
                "The Entra directory tenant identifier must be a specific organizational tenant.",
                nameof(tenantId));
        }

        DirectoryTenantId = tenantId;
        TenantIdValue = tenantId.ToString("D");
        ClientId = ValidateClientId(clientId);
        RequireHttps = requireHttps;
        Authority = ValidateAuthority(authority, tenantId, requireHttps);
        ExpectedIssuer = Authority.AbsoluteUri.TrimEnd('/');
        MetadataAddress = new Uri(
            string.Concat(ExpectedIssuer, "/", MetadataPath),
            UriKind.Absolute);
        RedirectUri = ValidateBrowserEndpoint(redirectUri, requireHttps, nameof(redirectUri));
        ApplicationBaseUri = ValidateApplicationBaseUri(
            applicationBaseUri,
            RedirectUri,
            requireHttps);
        PostLogoutRedirectUri = ValidatePostLogoutRedirectUri(
            postLogoutRedirectUri,
            ApplicationBaseUri,
            requireHttps);
        Scopes = Array.AsReadOnly(ValidateScopes(scopes));
        AllowedSigningAlgorithms = Array.AsReadOnly(
            ValidateSigningAlgorithms(allowedSigningAlgorithms));
        AllowedEndpointHosts = Array.AsReadOnly(new[] { Authority.Host });
        ClockSkew = ValidateRange(
            clockSkew ?? TimeSpan.FromMinutes(2),
            TimeSpan.Zero,
            MaximumClockSkew,
            nameof(clockSkew));
        HttpTimeout = ValidateRange(
            httpTimeout ?? TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(1),
            MaximumHttpTimeout,
            nameof(httpTimeout));
        MetadataCacheLifetime = ValidateRange(
            metadataCacheLifetime ?? TimeSpan.FromHours(12),
            MinimumMetadataCacheLifetime,
            MaximumMetadataCacheLifetime,
            nameof(metadataCacheLifetime));
        MetadataRefreshBackoff = ValidateRange(
            metadataRefreshBackoff ?? TimeSpan.FromMinutes(5),
            TimeSpan.FromSeconds(1),
            MetadataCacheLifetime,
            nameof(metadataRefreshBackoff));
        LoginRequestLifetime = ValidateRange(
            loginRequestLifetime ?? TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(1),
            MaximumLoginRequestLifetime,
            nameof(loginRequestLifetime));
    }

    public Guid DirectoryTenantId { get; }

    public string TenantIdValue { get; }

    public string ClientId { get; }

    public Uri Authority { get; }

    public Uri MetadataAddress { get; }

    public string ExpectedIssuer { get; }

    public Uri RedirectUri { get; }

    public Uri ApplicationBaseUri { get; }

    public Uri? PostLogoutRedirectUri { get; }

    public IReadOnlyList<string> Scopes { get; }

    public IReadOnlyList<string> AllowedSigningAlgorithms { get; }

    /// <summary>
    /// The only hosts a discovered authorization, token, or JWKS endpoint may
    /// use. Discovery documents are attacker-reachable input, so an endpoint
    /// that moves off the configured authority host is a server-side request
    /// forgery attempt rather than a provider migration.
    /// </summary>
    public IReadOnlyList<string> AllowedEndpointHosts { get; }

    public TimeSpan ClockSkew { get; }

    public TimeSpan HttpTimeout { get; }

    public TimeSpan MetadataCacheLifetime { get; }

    public TimeSpan MetadataRefreshBackoff { get; }

    public TimeSpan LoginRequestLifetime { get; }

    public bool RequireHttps { get; }

    public string ScopeParameter => string.Join(' ', Scopes);

    public override string ToString() => "[OidcProviderOptions REDACTED]";

    internal bool IsAllowedEndpoint(Uri? endpoint) =>
        endpoint is not null &&
        endpoint.IsAbsoluteUri &&
        IsApprovedScheme(endpoint, RequireHttps) &&
        string.IsNullOrEmpty(endpoint.Fragment) &&
        AllowedEndpointHosts.Contains(endpoint.Host, StringComparer.OrdinalIgnoreCase) &&
        endpoint.Port == Authority.Port &&
        string.IsNullOrEmpty(endpoint.UserInfo);

    private static string ValidateClientId(string clientId)
    {
        if (!Guid.TryParseExact(clientId?.Trim(), "D", out Guid parsed) ||
            parsed == Guid.Empty)
        {
            throw new ArgumentException(
                "The Entra application (client) identifier must be a GUID.",
                nameof(clientId));
        }

        return parsed.ToString("D");
    }

    private static Uri ValidateAuthority(Uri? authority, Guid tenantId, bool requireHttps)
    {
        if (authority is null)
        {
            return new Uri(
                $"https://{EntraLoginHost}/{tenantId:D}/v2.0",
                UriKind.Absolute);
        }

        if (!authority.IsAbsoluteUri ||
            !IsApprovedScheme(authority, requireHttps) ||
            !string.IsNullOrEmpty(authority.Query) ||
            !string.IsNullOrEmpty(authority.Fragment) ||
            !string.IsNullOrEmpty(authority.UserInfo))
        {
            throw new ArgumentException(
                "The OIDC authority must be an absolute HTTPS URL without credentials, query, or fragment.",
                nameof(authority));
        }

        string[] segments = authority.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(MultiTenantAliases.Contains))
        {
            throw new ArgumentException(
                "Multi-tenant and personal-account authority aliases are not supported.",
                nameof(authority));
        }

        if (string.Equals(authority.Host, EntraLoginHost, StringComparison.OrdinalIgnoreCase) &&
            (segments.Length != 2 ||
                !Guid.TryParseExact(segments[0], "D", out Guid authorityTenant) ||
                authorityTenant != tenantId ||
                !string.Equals(segments[1], "v2.0", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "An Entra authority must address the configured tenant with the v2.0 endpoint.",
                nameof(authority));
        }

        return authority;
    }

    private static Uri ValidateBrowserEndpoint(Uri endpoint, bool requireHttps, string parameterName)
    {
        if (endpoint is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!endpoint.IsAbsoluteUri ||
            !IsApprovedScheme(endpoint, requireHttps) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException(
                "Browser endpoints must be absolute HTTPS URLs without credentials, query, or fragment.",
                parameterName);
        }

        return endpoint;
    }

    private static Uri ValidateApplicationBaseUri(
        Uri? applicationBaseUri,
        Uri redirectUri,
        bool requireHttps)
    {
        if (applicationBaseUri is null)
        {
            return new Uri(redirectUri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        }

        Uri validated = ValidateBrowserEndpoint(
            applicationBaseUri,
            requireHttps,
            nameof(applicationBaseUri));
        if (!HasSameOrigin(validated, redirectUri) ||
            !validated.AbsolutePath.EndsWith('/') ||
            !redirectUri.AbsolutePath.StartsWith(validated.AbsolutePath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The application base URL must share the redirect URL origin and contain it.",
                nameof(applicationBaseUri));
        }

        return validated;
    }

    private static Uri? ValidatePostLogoutRedirectUri(
        Uri? postLogoutRedirectUri,
        Uri applicationBaseUri,
        bool requireHttps)
    {
        if (postLogoutRedirectUri is null)
        {
            return null;
        }

        Uri validated = ValidateBrowserEndpoint(
            postLogoutRedirectUri,
            requireHttps,
            nameof(postLogoutRedirectUri));
        if (!HasSameOrigin(validated, applicationBaseUri))
        {
            throw new ArgumentException(
                "The post-logout redirect URL must share the application origin.",
                nameof(postLogoutRedirectUri));
        }

        return validated;
    }

    private static string[] ValidateScopes(IReadOnlyCollection<string>? scopes)
    {
        IReadOnlyCollection<string> configured = scopes ?? DefaultScopes;
        string[] values = configured.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length != configured.Count ||
            !values.Contains("openid", StringComparer.Ordinal) ||
            values.Length > 20 ||
            values.Any(scope =>
                string.IsNullOrEmpty(scope) ||
                scope.Length > 128 ||
                scope.Any(character => character is <= ' ' or > '~' or '"' or '\\') ||
                string.Equals(scope, "offline_access", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Scopes must be a unique, printable allowlist that requests 'openid' and never 'offline_access'.",
                nameof(scopes));
        }

        return values;
    }

    private static string[] ValidateSigningAlgorithms(
        IReadOnlyCollection<string>? allowedSigningAlgorithms)
    {
        IReadOnlyCollection<string> configured =
            allowedSigningAlgorithms ?? [SecurityAlgorithms.RsaSha256];
        string[] values = configured.Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0 ||
            values.Length != configured.Count ||
            values.Any(algorithm => !SupportedSigningAlgorithms.Contains(algorithm)))
        {
            throw new ArgumentException(
                "Signing algorithms must be a unique allowlist of supported asymmetric algorithms.",
                nameof(allowedSigningAlgorithms));
        }

        return values;
    }

    private static TimeSpan ValidateRange(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        string parameterName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The value must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static bool IsApprovedScheme(Uri uri, bool requireHttps) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (!requireHttps && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.Ordinal) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}
