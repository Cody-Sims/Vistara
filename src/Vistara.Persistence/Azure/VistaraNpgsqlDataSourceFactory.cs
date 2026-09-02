using Azure.Core;
using Npgsql;

namespace Vistara.Persistence.Azure;

/// <summary>
/// Builds the <see cref="NpgsqlDataSource"/> that authenticates to Azure Database
/// for PostgreSQL with periodically refreshed Microsoft Entra ID access tokens.
/// </summary>
public static class VistaraNpgsqlDataSourceFactory
{
    public static NpgsqlDataSource Create(
        string connectionString,
        PersistenceAzureOptions options,
        TokenCredential credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        options.Validate();
        if (!options.EntraTokenEnabled)
        {
            throw new InvalidOperationException(
                $"'{PersistenceAzureOptions.SectionName}:EntraTokenEnabled' is false, so no "
                + "Entra-backed Npgsql data source may be built.");
        }

        ValidateConnectionString(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UsePeriodicPasswordProvider(
            CreateTokenProvider(options, credential),
            options.TokenRefreshInterval,
            options.TokenRetryInterval);
        return builder.Build();
    }

    /// <summary>
    /// Returns the periodic password provider. Every invocation asks the shared
    /// credential for a token so a rotated token reaches PostgreSQL without a
    /// process restart; failures and cancellation propagate so Npgsql retries on
    /// its configured interval instead of silently reusing a stale password.
    /// </summary>
    public static Func<NpgsqlConnectionStringBuilder, CancellationToken, ValueTask<string>>
        CreateTokenProvider(PersistenceAzureOptions options, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);

        var request = new TokenRequestContext([options.TokenScope]);
        return async (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            AccessToken token = await credential
                .GetTokenAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        };
    }

    /// <summary>
    /// Rejects connection strings that would weaken or bypass token
    /// authentication. Messages never echo the connection string because it may
    /// still carry the password that made it invalid.
    /// </summary>
    private static void ValidateConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidOperationException(
                "The PostgreSQL connection string is not a valid Npgsql connection string.",
                failure);
        }

        if (!string.IsNullOrEmpty(settings.Password))
        {
            throw new InvalidOperationException(
                "An Entra token connection string must not carry 'Password'; the token "
                + "provider supplies the password for every connection.");
        }

        if (!string.IsNullOrEmpty(settings.Passfile))
        {
            throw new InvalidOperationException(
                "An Entra token connection string must not carry 'Passfile'; the token "
                + "provider supplies the password for every connection.");
        }

        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new InvalidOperationException(
                "An Entra token connection string must set 'Host'.");
        }

        if (string.IsNullOrWhiteSpace(settings.Username))
        {
            throw new InvalidOperationException(
                "An Entra token connection string must set 'Username' to the PostgreSQL "
                + "role mapped to the managed identity.");
        }

        if (settings.SslMode != SslMode.VerifyFull)
        {
            throw new InvalidOperationException(
                "An Entra token connection string must set 'SSL Mode=VerifyFull'; weaker "
                + "modes let an interceptor collect the bearer token.");
        }

        // Npgsql defaults GSS encryption to Prefer, which makes every connection
        // attempt a Kerberos negotiation that slim runtime images cannot load.
        if (settings.GssEncryptionMode != GssEncryptionMode.Disable)
        {
            throw new InvalidOperationException(
                "An Entra token connection string must set 'GSS Encryption Mode=Disable'; "
                + "Entra tokens are presented as passwords over TLS, not through GSSAPI.");
        }
    }
}
