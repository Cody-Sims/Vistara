using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Npgsql;

namespace Vistara.Persistence.Azure;

/// <summary>
/// Owns the one managed-identity credential and the one
/// <see cref="NpgsqlDataSource"/> per connection string used by every PostgreSQL
/// call site. A credential per context would multiply identity-endpoint traffic
/// until Entra throttles the deployment, and a data source per context would
/// refresh tokens independently.
/// </summary>
public sealed class VistaraNpgsqlDataSourceProvider : IDisposable
{
    private readonly PersistenceAzureOptions _options;
    private readonly TokenCredential? _credential;
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _dataSources =
        new(StringComparer.Ordinal);

    private bool _disposed;

    public VistaraNpgsqlDataSourceProvider(PersistenceAzureOptions options)
        : this(options, credential: null)
    {
    }

    public VistaraNpgsqlDataSourceProvider(
        PersistenceAzureOptions options,
        TokenCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        _options = options;
        if (!options.EntraTokenEnabled)
        {
            return;
        }

        _credential = credential ?? new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId!));
    }

    public bool IsEnabled => _options.EntraTokenEnabled;

    public TokenCredential Credential =>
        _credential ?? throw new InvalidOperationException(
            $"'{PersistenceAzureOptions.SectionName}:EntraTokenEnabled' is false, so no "
            + "managed-identity credential exists.");

    /// <summary>
    /// Returns the shared data source for <paramref name="connectionString"/>, or
    /// null when Entra tokens are disabled and the caller should keep using the
    /// connection string directly.
    /// </summary>
    public NpgsqlDataSource? GetDataSource(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsEnabled)
        {
            return null;
        }

        return _dataSources.GetOrAdd(
            connectionString,
            static (key, state) => new Lazy<NpgsqlDataSource>(
                () => VistaraNpgsqlDataSourceFactory.Create(
                    key,
                    state._options,
                    state.Credential),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this).Value;
    }

    /// <summary>
    /// Fails closed when a non-PostgreSQL deployment enables Entra tokens; SQLite
    /// has no identity-backed authentication and would silently ignore them.
    /// </summary>
    public void EnsureSupports(VistaraDatabaseProvider databaseProvider)
    {
        if (IsEnabled && databaseProvider != VistaraDatabaseProvider.PostgreSql)
        {
            throw new InvalidOperationException(
                $"'{PersistenceAzureOptions.SectionName}:EntraTokenEnabled' is true, but the "
                + $"configured database provider is {databaseProvider}. Entra tokens require "
                + "PostgreSQL; SQLite deployments must leave the section absent.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Lazy<NpgsqlDataSource> dataSource in _dataSources.Values)
        {
            if (dataSource.IsValueCreated)
            {
                dataSource.Value.Dispose();
            }
        }

        _dataSources.Clear();
    }
}
