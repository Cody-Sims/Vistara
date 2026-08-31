using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Admin;
using Vistara.Application.Common.Storage;
using Vistara.Api.Features.Tenants;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Composes the real account surface over an in-memory SQLite database using
/// the production registration extensions, so the tests exercise the shipped
/// adapters rather than fakes.
/// </summary>
internal sealed class AccountSurfaceHarness : IAsyncDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly ServiceProvider _provider;

    private AccountSurfaceHarness(
        SqliteConnection anchor,
        ServiceProvider provider,
        string connectionString)
    {
        _anchor = anchor;
        _provider = provider;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal IServiceProvider Services => _provider;

    internal static async ValueTask<AccountSurfaceHarness> CreateAsync(
        Action<IServiceCollection>? configure = null)
    {
        string name = $"Account-{Guid.NewGuid():N}";
        string connectionString =
            $"Data Source={name};Mode=Memory;Cache=Shared;Default Timeout=30";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync(default);
        await using (VistaraDbContext schema = new(
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(connectionString)
                .Options,
            new FixedTenantScope(Guid.CreateVersion7())))
        {
            await schema.Database.EnsureCreatedAsync(default);
        }

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddVistaraPersistence(options =>
        {
            options.Provider = VistaraDatabaseProvider.Sqlite;
            options.ConnectionString = connectionString;
        });
        services.AddScoped<AmbientTenantScope>();
        services.AddScoped<ITenantScope>(
            provider => provider.GetRequiredService<AmbientTenantScope>());
        services.AddScoped<IMutableTenantScope>(
            provider => provider.GetRequiredService<AmbientTenantScope>());
        services.AddSingleton<ILocalPasswordHasher>(
            new Pbkdf2LocalPasswordHasher(100_000));
        services.AddSingleton<IBlobStore>(new ReachableBlobStore());
        configure?.Invoke(services);
        services.AddVistaraAccountSurface();
        services.AddVistaraTenantAdministration();
        services.AddVistaraAdministration();
        ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        return new AccountSurfaceHarness(anchor, provider, connectionString);
    }

    internal async Task<ProvisionedOwnerView> ProvisionAsync(
        string slug = "acme",
        string email = "owner@example.com",
        string password = "correct-horse-battery")
    {
        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        Result<ProvisionedOwnerView> provisioned = await scope.ServiceProvider
            .GetRequiredService<IFirstOwnerProvisioningPort>()
            .ProvisionAsync(
                new FirstOwnerProvisioningCommand(slug, slug, email, "Owner", password),
                default);
        Assert.True(
            provisioned.TryGetValue(out ProvisionedOwnerView? owner),
            provisioned.Error?.Message ?? "Provisioning failed.");
        return owner!;
    }

    /// <summary>
    /// Opens a scope whose ambient tenant context is already established, the
    /// way the platform middleware does for an authenticated request.
    /// </summary>
    internal AsyncServiceScope CreateTenantScope(Guid tenantId)
    {
        AsyncServiceScope scope = _provider.CreateAsyncScope();
        scope.ServiceProvider
            .GetRequiredService<AmbientTenantScope>()
            .Establish(tenantId);
        return scope;
    }

    internal VistaraDbContext CreateContext(Guid tenantId) =>
        new(
            new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite(ConnectionString)
                .Options,
            new FixedTenantScope(tenantId));

    /// <summary>Counts browser sessions for one tenant, optionally only live ones.</summary>
    internal async Task<int> CountCookieSessionsAsync(Guid tenantId, bool activeOnly)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(default);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = activeOnly
            ? "SELECT COUNT(*) FROM cookie_sessions WHERE lower(tenant_id) = $tenant AND revoked_at_utc IS NULL"
            : "SELECT COUNT(*) FROM cookie_sessions WHERE lower(tenant_id) = $tenant";
        command.Parameters.AddWithValue(
            "$tenant",
            tenantId.ToString("D").ToLowerInvariant());
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(default),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Reads the stored antiforgery digest for a browser session.</summary>
    internal async Task<string?> ReadAntiforgeryDigestAsync(string sessionToken)
    {
        string digest = CookieTokenCryptography.ComputeDigest(sessionToken);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(default);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT antiforgery_token_digest FROM cookie_sessions " +
            "WHERE session_token_digest = $digest AND revoked_at_utc IS NULL";
        command.Parameters.AddWithValue("$digest", digest);
        return await command.ExecuteScalarAsync(default) as string;
    }

    internal IdentityCatalogDbContext CreateCatalog() =>
        new(new DbContextOptionsBuilder<IdentityCatalogDbContext>()
            .UseSqlite(ConnectionString)
            .Options);

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _anchor.DisposeAsync();
    }

    /// <summary>
    /// A reachable local store: the administrative health probe only needs a
    /// name, capabilities, and a head that does not throw.
    /// </summary>
    internal sealed class ReachableBlobStore : IBlobStore
    {
        public string Name => "local";

        public BlobStoreCapabilities Capabilities { get; } = new()
        {
            SupportsRangeReads = true,
        };

        public ValueTask<BlobHead?> HeadAsync(
            BlobKey key,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<BlobHead?>(null);

        public ValueTask<BlobReadHandle> OpenReadAsync(
            BlobKey key,
            BlobReadOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BlobWriteResult> PutAsync(
            BlobKey key,
            IReplayableBlobContent content,
            BlobWriteOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BlobCopyResult> CopyAsync(
            BlobKey source,
            BlobKey destination,
            BlobCopyOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<BlobDeleteResult> DeleteAsync(
            BlobKey key,
            BlobDeleteOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<BlobHead> ListAsync(
            BlobListOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
            DirectUploadRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<MultipartSession> BeginMultipartAsync(
            MultipartRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
            MultipartSession session,
            int partNumber,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<MultipartCompletion> CompleteMultipartAsync(
            MultipartSession session,
            IReadOnlyList<UploadedPart> parts,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask AbortMultipartAsync(
            MultipartSession session,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
            BlobKey key,
            ReadGrantOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>
    /// Mirrors the request tenant context: it may be unset, and once set it
    /// cannot change during the request.
    /// </summary>
    internal sealed class AmbientTenantScope : IMutableTenantScope
    {
        private Guid _tenantId;

        public Guid TenantId => _tenantId;

        public void Establish(Guid tenantId)
        {
            if (_tenantId != Guid.Empty && _tenantId != tenantId)
            {
                throw new InvalidOperationException(
                    "Tenant context cannot change during a request.");
            }

            _tenantId = tenantId;
        }
    }
}
