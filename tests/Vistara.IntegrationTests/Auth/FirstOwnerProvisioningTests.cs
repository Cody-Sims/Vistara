using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Persistence;
using Vistara.Persistence.Auditing;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Repositories;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

public sealed class FirstOwnerProvisioningTests
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task Provisioning_creates_an_active_owner_with_a_verifiable_password()
    {
        await using var database = await ProvisioningDatabase.CreateAsync();

        Result<ProvisionedOwnerView> provisioned = await database
            .CreateProvisioning()
            .ProvisionAsync(Command(), default);

        Assert.True(provisioned.TryGetValue(out ProvisionedOwnerView? owner));
        Assert.Equal("acme", owner.TenantSlug);
        Assert.Equal("owner@example.com", owner.Email);
        Assert.Equal("TenantOwner", owner.Role);

        await using VistaraDbContext read = database.CreateContext(owner.TenantId);
        Assert.Equal(1, await read.Tenants.CountAsync(default));
        Assert.Equal(1, await read.Users.CountAsync(default));
        Assert.Equal(1, await read.LocalIdentities.CountAsync(default));
        Assert.Equal("Active", await read.TenantMemberships
            .Where(row => row.UserId == owner.UserId)
            .Select(row => row.Status)
            .SingleAsync(default));
        string storedHash = await read.LocalCredentials
            .Select(row => row.PasswordHash)
            .SingleAsync(default);
        Assert.DoesNotContain(Password, storedHash, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$", storedHash, StringComparison.Ordinal);
        Assert.Equal(1, await read.AuditEvents.CountAsync(default));
    }

    [Fact]
    public async Task Provisioning_is_refused_after_the_first_owner_exists()
    {
        await using var database = await ProvisioningDatabase.CreateAsync();
        Result<ProvisionedOwnerView> first = await database
            .CreateProvisioning()
            .ProvisionAsync(Command(), default);
        Assert.True(first.IsSuccess);

        Result<ProvisionedOwnerView> second = await database
            .CreateProvisioning()
            .ProvisionAsync(
                Command("second", "second@example.com"),
                default);

        Assert.True(second.IsFailure);
        Assert.Equal("setup.already_provisioned", second.Error!.Code);
        Assert.Equal(ErrorCategory.Conflict, second.Error.Category);
    }

    [Fact]
    public async Task Provisioning_rejects_a_short_password_before_writing_anything()
    {
        await using var database = await ProvisioningDatabase.CreateAsync();

        Result<ProvisionedOwnerView> provisioned = await database
            .CreateProvisioning()
            .ProvisionAsync(Command(password: "short"), default);

        Assert.True(provisioned.IsFailure);
        Assert.Equal("setup.weak_password", provisioned.Error!.Code);
        await using IdentityCatalogDbContext catalog = database.CreateCatalog();
        Assert.Equal(0, await catalog.Users.CountAsync(default));
    }

    [Fact]
    public async Task The_provisioned_password_verifies_and_wrong_passwords_do_not()
    {
        await using var database = await ProvisioningDatabase.CreateAsync();
        Result<ProvisionedOwnerView> provisioned = await database
            .CreateProvisioning()
            .ProvisionAsync(Command(), default);
        Assert.True(provisioned.TryGetValue(out ProvisionedOwnerView? owner));

        var verifier = new PlatformLocalCredentialVerifier(
            new RelationalIdentityCatalog(database.CreateCatalog()),
            database.Hasher,
            new DummyLocalPasswordVerifier(database.Hasher));

        User? matched = await verifier.VerifyAsync(
            "owner@example.com",
            Password,
            default);
        User? mismatched = await verifier.VerifyAsync(
            "owner@example.com",
            "wrong-password-value",
            default);
        User? unknown = await verifier.VerifyAsync(
            "nobody@example.com",
            Password,
            default);

        Assert.NotNull(matched);
        Assert.Equal(owner.UserId, matched.Id.Value);
        Assert.Equal(UserStatus.Active, matched.Status);
        Assert.Null(mismatched);
        Assert.Null(unknown);
    }

    private static FirstOwnerProvisioningCommand Command(
        string slug = "acme",
        string email = "owner@example.com",
        string password = Password) =>
        new(slug, "Acme", email, "Owner", password);

    private sealed class ProvisioningDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;

        private ProvisioningDatabase(SqliteConnection anchor, string connectionString)
        {
            _anchor = anchor;
            _connectionString = connectionString;
        }

        internal ILocalPasswordHasher Hasher { get; } =
            new Pbkdf2LocalPasswordHasher(100_000);

        internal static async ValueTask<ProvisioningDatabase> CreateAsync()
        {
            string name = $"Provisioning-{Guid.NewGuid():N}";
            string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync(default);
            var database = new ProvisioningDatabase(anchor, connectionString);
            await using VistaraDbContext schema =
                database.CreateContext(Guid.CreateVersion7());
            await schema.Database.EnsureCreatedAsync(default);
            return database;
        }

        internal VistaraDbContext CreateContext(Guid tenantId) =>
            new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(_connectionString)
                    .Options,
                new FixedTenantScope(tenantId));

        internal VistaraDbContext CreateContext(MutableTenantScope scope) =>
            new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(_connectionString)
                    .Options,
                scope);

        internal IdentityCatalogDbContext CreateCatalog() =>
            new(new DbContextOptionsBuilder<IdentityCatalogDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        internal PlatformFirstOwnerProvisioningAdapter CreateProvisioning()
        {
            var scope = new MutableTenantScope();
            VistaraDbContext context = CreateContext(scope);
            IClock clock = SystemClock.Instance;
            IUuid7Generator ids = new Uuid7Generator(clock);
            return new PlatformFirstOwnerProvisioningAdapter(
                new RelationalIdentityCatalog(CreateCatalog()),
                new TenantRepository(context),
                new UserRepository(context),
                new TenantMembershipRepository(context),
                new TenantFactory(ids, clock),
                new IdentityFactory(ids, clock),
                Hasher,
                new RelationalAuditWriter(context),
                scope,
                ids,
                clock);
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }

    private sealed class MutableTenantScope : IMutableTenantScope
    {
        public Guid TenantId { get; private set; }

        public void Establish(Guid tenantId) => TenantId = tenantId;
    }
}
