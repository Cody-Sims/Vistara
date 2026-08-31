using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Vistara.Api.Composition.Platform;
using Vistara.Auth.Cookies;
using Vistara.Domain.Identity;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

/// <summary>
/// Proves that an absent login performs the same key-derivation work as a
/// present login with a wrong password, without relying on wall-clock
/// thresholds.
/// </summary>
public sealed class LocalCredentialVerifierTimingTests
{
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task Absent_and_present_logins_run_exactly_one_derivation_each()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        var hasher = new CountingPasswordHasher(new Pbkdf2LocalPasswordHasher(100_000));
        var dummy = new DummyLocalPasswordVerifier(hasher);
        await database.SeedAsync(hasher.Hash(Password));
        hasher.Reset();
        var verifier = new PlatformLocalCredentialVerifier(
            new RelationalIdentityCatalog(database.CreateCatalog()),
            hasher,
            dummy);

        User? present = await verifier.VerifyAsync(
            "owner@example.com",
            "wrong-password-value",
            default);
        int derivationsForPresent = hasher.Verifications;
        hasher.Reset();

        User? absent = await verifier.VerifyAsync(
            "nobody@example.com",
            "wrong-password-value",
            default);
        int derivationsForAbsent = hasher.Verifications;

        Assert.Null(present);
        Assert.Null(absent);
        Assert.Equal(1, derivationsForPresent);
        Assert.Equal(derivationsForPresent, derivationsForAbsent);
    }

    [Fact]
    public async Task An_invalid_login_shape_still_runs_one_derivation()
    {
        await using var database = await CredentialDatabase.CreateAsync();
        var hasher = new CountingPasswordHasher(new Pbkdf2LocalPasswordHasher(100_000));
        var dummy = new DummyLocalPasswordVerifier(hasher);
        await database.SeedAsync(hasher.Hash(Password));
        hasher.Reset();
        var verifier = new PlatformLocalCredentialVerifier(
            new RelationalIdentityCatalog(database.CreateCatalog()),
            hasher,
            dummy);

        User? result = await verifier.VerifyAsync(
            new string('a', 400),
            "wrong-password-value",
            default);

        Assert.Null(result);
        Assert.Equal(1, hasher.Verifications);
    }

    [Fact]
    public void The_dummy_verifier_is_a_real_pbkdf2_record_that_never_matches()
    {
        var hasher = new Pbkdf2LocalPasswordHasher(100_000);
        var dummy = new DummyLocalPasswordVerifier(hasher);

        string verifier = dummy.Verifier;

        Assert.StartsWith("pbkdf2-sha256$", verifier, StringComparison.Ordinal);
        Assert.Equal(4, verifier.Split('$').Length);
        Assert.False(dummy.ConsumeVerification(Password));
        Assert.False(dummy.ConsumeVerification(string.Empty));
        Assert.False(hasher.Verify(Password, verifier));
    }

    [Fact]
    public void The_dummy_verifier_derives_its_record_only_once()
    {
        var hasher = new CountingPasswordHasher(new Pbkdf2LocalPasswordHasher(100_000));
        var dummy = new DummyLocalPasswordVerifier(hasher);

        _ = dummy.Verifier;
        _ = dummy.Verifier;
        _ = dummy.ConsumeVerification("first");
        _ = dummy.ConsumeVerification("second");

        Assert.Equal(1, hasher.Hashes);
        Assert.Equal(2, hasher.Verifications);
    }

    private sealed class CountingPasswordHasher(ILocalPasswordHasher inner)
        : ILocalPasswordHasher
    {
        private int _hashes;
        private int _verifications;

        public int Hashes => Volatile.Read(ref _hashes);

        public int Verifications => Volatile.Read(ref _verifications);

        public int MinimumPasswordLength => inner.MinimumPasswordLength;

        public string Hash(string password)
        {
            Interlocked.Increment(ref _hashes);
            return inner.Hash(password);
        }

        public bool Verify(string password, string storedHash)
        {
            Interlocked.Increment(ref _verifications);
            return inner.Verify(password, storedHash);
        }

        public void Reset()
        {
            Volatile.Write(ref _hashes, 0);
            Volatile.Write(ref _verifications, 0);
        }
    }

    private sealed class CredentialDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly string _connectionString;

        private CredentialDatabase(SqliteConnection anchor, string connectionString)
        {
            _anchor = anchor;
            _connectionString = connectionString;
        }

        internal static async ValueTask<CredentialDatabase> CreateAsync()
        {
            string name = $"Credentials-{Guid.NewGuid():N}";
            string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync(default);
            var database = new CredentialDatabase(anchor, connectionString);
            await using VistaraDbContext schema = new(
                new DbContextOptionsBuilder<VistaraDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                new FixedTenantScope(Guid.CreateVersion7()));
            await schema.Database.EnsureCreatedAsync(default);
            return database;
        }

        internal IdentityCatalogDbContext CreateCatalog() =>
            new(new DbContextOptionsBuilder<IdentityCatalogDbContext>()
                .UseSqlite(_connectionString)
                .Options);

        internal async Task SeedAsync(string passwordHash)
        {
            Guid userId = Guid.CreateVersion7();
            Guid identityId = Guid.CreateVersion7();
            DateTimeOffset now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);
            await using IdentityCatalogDbContext catalog = CreateCatalog();
            catalog.Users.Add(new UserRow
            {
                Id = userId,
                NormalizedEmail = "owner@example.com",
                DisplayName = "Owner",
                Status = "Active",
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            await catalog.SaveChangesAsync(default);
            catalog.LocalIdentities.Add(new LocalIdentityRow
            {
                Id = identityId,
                UserId = userId,
                NormalizedLogin = "owner@example.com",
                LinkedAtUtc = now,
            });
            await catalog.SaveChangesAsync(default);
            catalog.LocalCredentials.Add(new LocalCredentialRow
            {
                LocalIdentityId = identityId,
                UserId = userId,
                PasswordHash = passwordHash,
                UpdatedAtUtc = now,
                Version = 1,
            });
            await catalog.SaveChangesAsync(default);
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }
}
