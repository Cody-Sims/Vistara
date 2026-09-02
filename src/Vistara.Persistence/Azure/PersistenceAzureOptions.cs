using Microsoft.Extensions.Configuration;

namespace Vistara.Persistence.Azure;

/// <summary>
/// Optional Microsoft Entra ID token settings for PostgreSQL connections
/// (<c>Persistence:Azure</c>). Absent or disabled configuration keeps the
/// deployment on password connection strings and SQLite.
/// </summary>
public sealed class PersistenceAzureOptions
{
    public const string SectionName = "Persistence:Azure";

    public const string DefaultTokenScope =
        "https://ossrdbms-aad.database.windows.net/.default";

    public static readonly TimeSpan DefaultTokenRefreshInterval =
        TimeSpan.FromMinutes(55);

    public static readonly TimeSpan DefaultTokenRetryInterval =
        TimeSpan.FromSeconds(5);

    // Entra access tokens live between five and sixty minutes, so a refresh
    // outside that window either hammers the identity endpoint or hands Npgsql
    // an expired password.
    private static readonly TimeSpan MinimumTokenRefreshInterval =
        TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaximumTokenRefreshInterval =
        TimeSpan.FromMinutes(60);

    private static readonly TimeSpan MaximumTokenRetryInterval =
        TimeSpan.FromMinutes(5);

    private const string ScopeSuffix = "/.default";

    public bool EntraTokenEnabled { get; set; }

    public string? ManagedIdentityClientId { get; set; }

    public TimeSpan TokenRefreshInterval { get; set; } = DefaultTokenRefreshInterval;

    public TimeSpan TokenRetryInterval { get; set; } = DefaultTokenRetryInterval;

    public string TokenScope { get; set; } = DefaultTokenScope;

    /// <summary>
    /// Reads and validates <c>Persistence:Azure</c>. A null configuration or a
    /// missing section leaves Entra tokens disabled.
    /// </summary>
    public static PersistenceAzureOptions FromConfiguration(IConfiguration? configuration)
    {
        var options = new PersistenceAzureOptions();
        configuration?.GetSection(SectionName).Bind(options);
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!EntraTokenEnabled)
        {
            ValidateDisabled();
            return;
        }

        if (!Guid.TryParse(ManagedIdentityClientId, out Guid clientId) ||
            clientId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:ManagedIdentityClientId' must be the client id GUID of "
                + "the user-assigned managed identity that owns the PostgreSQL role.");
        }

        if (string.IsNullOrWhiteSpace(TokenScope) ||
            !Uri.TryCreate(TokenScope, UriKind.Absolute, out Uri? scope) ||
            !scope.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !TokenScope.EndsWith(ScopeSuffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{SectionName}:TokenScope' must be an absolute HTTPS scope ending in "
                + $"'{ScopeSuffix}', such as '{DefaultTokenScope}'.");
        }

        if (TokenRefreshInterval < MinimumTokenRefreshInterval ||
            TokenRefreshInterval > MaximumTokenRefreshInterval)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:TokenRefreshInterval' must be between "
                + $"{MinimumTokenRefreshInterval} and {MaximumTokenRefreshInterval} so a "
                + "refreshed token is always inside the Entra token lifetime.");
        }

        if (TokenRetryInterval <= TimeSpan.Zero ||
            TokenRetryInterval > MaximumTokenRetryInterval ||
            TokenRetryInterval > TokenRefreshInterval)
        {
            throw new InvalidOperationException(
                $"'{SectionName}:TokenRetryInterval' must be positive, at most "
                + $"{MaximumTokenRetryInterval}, and no longer than "
                + $"'{SectionName}:TokenRefreshInterval'.");
        }
    }

    private void ValidateDisabled()
    {
        if (!string.IsNullOrWhiteSpace(ManagedIdentityClientId) ||
            !string.Equals(TokenScope, DefaultTokenScope, StringComparison.Ordinal) ||
            TokenRefreshInterval != DefaultTokenRefreshInterval ||
            TokenRetryInterval != DefaultTokenRetryInterval)
        {
            throw new InvalidOperationException(
                $"'{SectionName}' carries Entra token settings while "
                + $"'{SectionName}:EntraTokenEnabled' is false; enable it explicitly or "
                + "remove the section so password authentication stays intentional.");
        }
    }
}
