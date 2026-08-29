using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vistara.Application.Common;
using Vistara.Application.Uploads.Quotas;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Domain.Uploads;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Repositories;
using Vistara.Persistence.Uploads;
using Xunit;

namespace Vistara.MigrationTests;

public sealed class LegacyUpgradeTests
{
    private static readonly Guid TenantId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000101");
    private static readonly Guid FirstActorId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000102");
    private static readonly Guid SecondActorId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000103");
    private static readonly Guid MultipartUploadId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000104");
    private static readonly Guid DirectUploadId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000105");
    private static readonly Guid MultipartReservationId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000106");
    private static readonly Guid DirectReservationId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000107");
    private static readonly Guid StandaloneReservationId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000108");
    private static readonly Guid AuthSessionId =
        Guid.Parse("01991a54-6c00-7000-8000-000000000109");
    private static readonly Guid ApiKeyId =
        Guid.Parse("01991a54-6c00-7000-8000-00000000010a");

    [Fact]
    public async Task Initial_data_upgrades_without_collisions_and_hydrates()
    {
        string connectionString = SharedMemoryConnectionString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using VistaraDbContext context =
            MigrationTestSupport.CreateSqliteContext(anchor, TenantId);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationTestSupport.InitialMigration);
        await SeedInitialDataAsync(anchor);

        await migrator.MigrateAsync();

        Assert.Equal(
            MigrationTestSupport.SqliteMigrations,
            await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                anchor,
                "SELECT COUNT(*) FROM quota_reservations;"));
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                anchor,
                """
                SELECT COUNT(*)
                FROM quota_reservations
                WHERE created_at_utc < expires_at_utc;
                """));
        Assert.Equal(
            $"upload:{FirstActorId:N}:shared-key",
            await ScalarAsync<string>(
                anchor,
                $"SELECT idempotency_key FROM quota_reservations WHERE id = '{MultipartReservationId:D}';"));
        Assert.Equal(
            $"upload:{SecondActorId:N}:shared-key",
            await ScalarAsync<string>(
                anchor,
                $"SELECT idempotency_key FROM quota_reservations WHERE id = '{DirectReservationId:D}';"));
        Assert.Equal(
            $"legacy-reservation:{StandaloneReservationId:N}",
            await ScalarAsync<string>(
                anchor,
                $"SELECT idempotency_key FROM quota_reservations WHERE id = '{StandaloneReservationId:D}';"));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                anchor,
                """
                SELECT COUNT(DISTINCT idempotency_key)
                FROM quota_reservations
                WHERE idempotency_key LIKE 'upload:%:shared-key';
                """));

        Assert.Equal(
            "Expired",
            await ScalarAsync<string>(
                anchor,
                $"SELECT state FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            "UploadIssued",
            await ScalarAsync<string>(
                anchor,
                $"SELECT last_known_state FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                $"SELECT provider_upload_id IS NULL FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            "Expired",
            await ScalarAsync<string>(
                anchor,
                $"SELECT state FROM quota_reservations WHERE id = '{MultipartReservationId:D}';"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                """
                SELECT COUNT(*)
                FROM quota_usage
                WHERE tenant_id = '01991a54-6c00-7000-8000-000000000101'
                  AND reserved_uploads = 1
                  AND reserved_bytes = 41
                  AND reserved_objects = 2
                  AND reserved_compute_units = 3;
                """));

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                $"""
                 SELECT COUNT(*)
                 FROM authentication_routes
                 WHERE kind = 'ApiKey'
                   AND routed_tenant_id = '{TenantId:D}'
                   AND principal_id = '{FirstActorId:D}'
                   AND credential_id = '{ApiKeyId:D}'
                   AND length(lookup_digest) = 64
                   AND lookup_digest NOT GLOB '*[^0-9a-f]*';
                 """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                anchor,
                "SELECT COUNT(*) FROM cookie_sessions;"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                anchor,
                """
                SELECT COUNT(*)
                FROM authentication_routes
                WHERE kind = 'CookieSession';
                """));

        context.ChangeTracker.Clear();
        var tenants = new TenantRepository(context);
        var users = new UserRepository(context);
        var memberships = new TenantMembershipRepository(context);
        var authSessions = new AuthSessionRepository(context);
        var uploads = new UploadSessionRepository(context);

        Assert.NotNull(await tenants.FindByIdAsync(
            new TenantId(TenantId),
            CancellationToken.None));
        Assert.NotNull(await users.FindByIdAsync(
            new UserId(FirstActorId),
            CancellationToken.None));
        Assert.NotNull(await memberships.FindAsync(
            new TenantId(TenantId),
            new UserId(FirstActorId),
            CancellationToken.None));
        Assert.NotNull(await authSessions.FindByDigestAsync(
            new SessionDigest(new string('c', 64)),
            CancellationToken.None));

        UploadSession multipart = Assert.IsType<UploadSession>(
            await uploads.FindByIdempotencyAsync(
                TenantId,
                FirstActorId,
                "shared-key",
                CancellationToken.None));
        Assert.Equal(UploadState.Expired, multipart.State);
        Assert.Null(multipart.ProviderUploadId);
        Assert.Equal(
            UploadReservationState.Expired,
            multipart.Reservation.State);

        UploadSession direct = Assert.IsType<UploadSession>(
            await uploads.FindByIdempotencyAsync(
                TenantId,
                SecondActorId,
                "shared-key",
                CancellationToken.None));
        Assert.Equal(UploadState.Pending, direct.State);

        var catalogOptions =
            new DbContextOptionsBuilder<AuthenticationCatalogDbContext>()
                .UseSqlite(connectionString)
                .Options;
        var revocationOptions =
            new DbContextOptionsBuilder<JwtRevocationCatalogDbContext>()
                .UseSqlite(connectionString)
                .Options;
        await using var catalog =
            new AuthenticationCatalogDbContext(catalogOptions);
        await using var revocations =
            new JwtRevocationCatalogDbContext(revocationOptions);
        var requestTenant = new MutableTenantScope();
        var authentication = new RelationalAuthenticationStore(
            catalog,
            revocations,
            new TenantDbContextFactory(new VistaraPersistenceOptions
            {
                Provider = VistaraDatabaseProvider.Sqlite,
                ConnectionString = connectionString,
            }),
            requestTenant);

        PersistedApiKeyAuthentication authenticated = Assert.IsType<
            PersistedApiKeyAuthentication>(
            await authentication.FindApiKeyForAuthenticationAsync(
                ApiKeyId,
                CancellationToken.None));
        Assert.Equal(ApiKeyId, authenticated.Metadata.Id.Value);
        Assert.Equal(TenantId, authenticated.Metadata.TenantId.Value);
        Assert.Equal(FirstActorId, authenticated.Metadata.OwnerId.Value);

        var quota = new RelationalQuotaReservationStore(context);
        QuotaStoreTransitionResult released = await quota.TransitionAsync(
            new AtomicQuotaTransition(
                StandaloneReservationId,
                QuotaReservationState.Released,
                ExpectedVersion: 1,
                new DateTimeOffset(
                    2026,
                    8,
                    29,
                    1,
                    30,
                    0,
                    TimeSpan.Zero)),
            CancellationToken.None);
        Assert.Equal(QuotaStoreTransitionStatus.Transitioned, released.Status);
        Assert.Equal(
            $"legacy-reservation:{StandaloneReservationId:N}",
            released.Reservation?.IdempotencyKey);
        Assert.True(
            released.Reservation?.CreatedAtUtc <
            released.Reservation?.ExpiresAtUtc);
    }

    [Fact]
    public async Task Upload_ingest_baseline_multipart_state_becomes_reconcilable()
    {
        string connectionString = SharedMemoryConnectionString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using VistaraDbContext context =
            MigrationTestSupport.CreateSqliteContext(anchor, TenantId);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(
            MigrationTestSupport.SqliteUploadIngestMigration);
        await SeedUploadIngestMultipartAsync(anchor);

        await migrator.MigrateAsync();

        Assert.Equal(
            "s3:v1:legacy-s3-upload",
            await ScalarAsync<string>(
                anchor,
                $"SELECT multipart_provider_state FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                $"SELECT multipart_part_plan_lifetime_ticks FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            "OutcomeUnknown",
            await ScalarAsync<string>(
                anchor,
                $"SELECT state FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            "Aborting",
            await ScalarAsync<string>(
                anchor,
                $"SELECT last_known_state FROM upload_sessions WHERE id = '{MultipartUploadId:D}';"));
        Assert.Equal(
            "Reserved",
            await ScalarAsync<string>(
                anchor,
                $"SELECT state FROM quota_reservations WHERE id = '{MultipartReservationId:D}';"));
        Assert.Equal(
            $"upload:{FirstActorId:N}:known-key",
            await ScalarAsync<string>(
                anchor,
                $"SELECT idempotency_key FROM quota_reservations WHERE id = '{MultipartReservationId:D}';"));
        Assert.Equal(
            $"job:{FirstActorId:N}:work",
            await ScalarAsync<string>(
                anchor,
                $"SELECT idempotency_key FROM quota_reservations WHERE id = '{StandaloneReservationId:D}';"));

        context.ChangeTracker.Clear();
        var store = new RelationalUploadReconciliationStore(
            context,
            new FixedUuid7Generator());
        PersistedUploadReconciliationCandidate candidate = Assert.Single(
            (await store.ScanAsync(
                TenantId,
                cursor: null,
                maximumSessions: 10,
                new DateTimeOffset(
                    2026,
                    8,
                    30,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                TimeSpan.FromMinutes(5),
                dryRun: false,
                CancellationToken.None)).Candidates);
        Assert.Equal("OutcomeUnknown", candidate.State);
        Assert.Equal("Aborting", candidate.LastKnownState);
        Assert.False(candidate.ReservationReleased);
        Assert.NotNull(candidate.MultipartSession);
        Assert.Equal("legacy-s3-upload", candidate.MultipartSession.UploadId);
        Assert.Equal("s3:v1:legacy-s3-upload", candidate.MultipartSession.ProviderState);
        Assert.Equal(10_000, candidate.MultipartSession.MaxParts);
        Assert.Equal(5L * 1024 * 1024, candidate.MultipartSession.MinPartBytes);
        Assert.Equal(5L * 1024 * 1024 * 1024, candidate.MultipartSession.MaxPartBytes);
    }

    [Fact]
    public async Task Data_normalization_rolls_back_and_reapplies_idempotently()
    {
        string connectionString = SharedMemoryConnectionString();
        await using var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        await using VistaraDbContext context =
            MigrationTestSupport.CreateSqliteContext(anchor, TenantId);
        IMigrator migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(MigrationTestSupport.InitialMigration);
        await SeedInitialDataAsync(anchor);
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(
            MigrationTestSupport.SqliteRuntimeReconciliationMigration);
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                "SELECT COUNT(*) FROM authentication_routes;"));
        Assert.Equal(
            3L,
            await ScalarAsync<long>(
                anchor,
                "SELECT COUNT(*) FROM quota_reservations;"));

        await migrator.MigrateAsync();

        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                anchor,
                "SELECT COUNT(*) FROM authentication_routes;"));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                anchor,
                """
                SELECT COUNT(DISTINCT idempotency_key)
                FROM quota_reservations
                WHERE idempotency_key LIKE 'upload:%:shared-key';
                """));
    }

    private static async Task SeedInitialDataAsync(SqliteConnection connection)
    {
        await ExecuteAsync(
            connection,
            $"""
             INSERT INTO tenants (
                 id, tenant_id, slug, name, status, settings_json, quotas_json,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 '{TenantId:D}', '{TenantId:D}', 'legacy', 'Legacy', 'Active',
                 char(123) || char(125), char(123) || char(125),
                 '2026-08-29T00:00:00Z',
                 '2026-08-29T00:00:00Z', 1);

             INSERT INTO users (
                 id, normalized_email, display_name, status,
                 created_at_utc, updated_at_utc, version)
             VALUES
                 ('{FirstActorId:D}', 'first@example.test', 'First', 'Active',
                  '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z', 1),
                 ('{SecondActorId:D}', 'second@example.test', 'Second', 'Active',
                  '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z', 1);

             INSERT INTO tenant_memberships (
                 tenant_id, user_id, role, status, invited_at_utc,
                 joined_at_utc, updated_at_utc, version)
             VALUES
                 ('{TenantId:D}', '{FirstActorId:D}', 'TenantOwner', 'Active',
                  '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z',
                  '2026-08-29T00:00:00Z', 1),
                 ('{TenantId:D}', '{SecondActorId:D}', 'Member', 'Active',
                  '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z',
                  '2026-08-29T00:00:00Z', 1);

             INSERT INTO auth_sessions (
                 id, user_id, digest, created_at_utc, expires_at_utc,
                 updated_at_utc, version)
             VALUES (
                 '{AuthSessionId:D}', '{FirstActorId:D}', '{new string('c', 64)}',
                 '2026-08-29T00:00:00Z', '2026-08-30T00:00:00Z',
                 '2026-08-29T00:00:00Z', 1);

             INSERT INTO api_keys (
                 id, tenant_id, owner_id, prefix, digest, scopes,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 '{ApiKeyId:D}', '{TenantId:D}', '{FirstActorId:D}', 'vst_legacy',
                 '{new string('d', 64)}', 1, '2026-08-29T00:00:00Z',
                 '2026-08-29T00:00:00Z', 1);

             INSERT INTO upload_sessions (
                 id, tenant_id, actor_id, strategy, staging_key,
                 provider_upload_id, expected_bytes, expected_sha256,
                 declared_content_type, state, expires_at_utc,
                 created_at_utc, updated_at_utc, version)
             VALUES
                 ('{MultipartUploadId:D}', '{TenantId:D}', '{FirstActorId:D}',
                  'Multipart', 'staging/multipart', 'legacy-provider-upload',
                  17, '{new string('a', 64)}', 'image/png', 'UploadIssued',
                  '2026-08-29T01:00:00Z', '2026-08-29T00:00:00Z',
                  '2026-08-29T00:05:00Z', 2),
                 ('{DirectUploadId:D}', '{TenantId:D}', '{SecondActorId:D}',
                  'Direct', 'staging/direct', NULL, 23, '{new string('b', 64)}',
                  'image/jpeg', 'Pending', '2026-08-29T01:00:00Z',
                  '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z', 1);

             INSERT INTO idempotency_requests (
                 tenant_id, principal_id, key, request_hash,
                 upload_session_id, response_reference, expires_at_utc)
             VALUES
                 ('{TenantId:D}', '{FirstActorId:D}', 'shared-key',
                  '{new string('1', 64)}', '{MultipartUploadId:D}',
                  '{MultipartUploadId:D}', '2026-08-29T01:00:00Z'),
                 ('{TenantId:D}', '{SecondActorId:D}', 'shared-key',
                  '{new string('2', 64)}', '{DirectUploadId:D}',
                  '{DirectUploadId:D}', '2026-08-29T01:00:00Z');

             INSERT INTO quota_reservations (
                 id, tenant_id, upload_session_id, reserved_bytes,
                 reserved_objects, reserved_compute_units, state, expires_at_utc)
             VALUES
                 ('{MultipartReservationId:D}', '{TenantId:D}',
                  '{MultipartUploadId:D}', 17, 1, 2, 'Reserved',
                  '2026-08-29T01:00:00Z'),
                 ('{DirectReservationId:D}', '{TenantId:D}',
                  '{DirectUploadId:D}', 23, 1, 1, 'Reserved',
                  '2026-08-29T01:00:00Z'),
                 ('{StandaloneReservationId:D}', '{TenantId:D}',
                  NULL, 18, 1, 2, 'Reserved', '2026-08-29T02:00:00Z');
             """);
    }

    private static async Task SeedUploadIngestMultipartAsync(
        SqliteConnection connection)
    {
        await ExecuteAsync(
            connection,
            $"""
             INSERT INTO tenants (
                 id, tenant_id, slug, name, status, settings_json, quotas_json,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 '{TenantId:D}', '{TenantId:D}', 'n-minus-one', 'N minus one',
                 'Active', char(123) || char(125), char(123) || char(125),
                 '2026-08-29T00:00:00Z',
                 '2026-08-29T00:00:00Z', 1);

             INSERT INTO users (
                 id, normalized_email, display_name, status,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 '{FirstActorId:D}', 'owner@example.test', 'Owner', 'Active',
                 '2026-08-29T00:00:00Z', '2026-08-29T00:00:00Z', 1);

             INSERT INTO upload_sessions (
                 id, tenant_id, actor_id, display_file_name, strategy,
                 staging_key, storage_provider, storage_container,
                 provider_upload_id, multipart_expires_at_utc,
                 multipart_max_parts, multipart_min_part_bytes,
                 multipart_max_part_bytes, expected_bytes, expected_sha256,
                 declared_content_type, state, expires_at_utc,
                 created_at_utc, updated_at_utc, version)
             VALUES (
                 '{MultipartUploadId:D}', '{TenantId:D}', '{FirstActorId:D}',
                 'legacy.png', 'Multipart', 'staging/known',
                 'aws-s3', 'media', 'legacy-s3-upload',
                 '2026-08-29T01:00:00Z', 10000, 5242880, 5368709120,
                 17, '{new string('a', 64)}', 'image/png', 'UploadIssued',
                 '2026-08-29T01:00:00Z', '2026-08-29T00:00:00Z',
                 '2026-08-29T00:05:00Z', 2);

             INSERT INTO idempotency_requests (
                 tenant_id, principal_id, key, request_hash,
                 upload_session_id, response_reference, expires_at_utc)
             VALUES (
                 '{TenantId:D}', '{FirstActorId:D}', 'known-key',
                 '{new string('1', 64)}', '{MultipartUploadId:D}',
                 '{MultipartUploadId:D}', '2026-08-29T01:00:00Z');

             INSERT INTO quota_reservations (
                 id, tenant_id, upload_session_id, idempotency_key,
                 request_fingerprint, reserved_uploads, reserved_bytes,
                 reserved_objects, reserved_compute_units, reserved_jobs,
                 reserved_budget_units, state, created_at_utc, expires_at_utc,
                 updated_at_utc, version)
             VALUES
                 ('{MultipartReservationId:D}', '{TenantId:D}',
                  '{MultipartUploadId:D}',
                  'known-key', '{new string('1', 64)}',
                  1, 17, 1, 2, 0, 0, 'Reserved',
                  '2026-08-29T00:00:00Z', '2026-08-29T01:00:00Z',
                  '2026-08-29T00:00:00Z', 1),
                 ('{StandaloneReservationId:D}', '{TenantId:D}', NULL,
                  'job:{FirstActorId:N}:work', '{new string('3', 64)}',
                  0, 5, 1, 0, 0, 0, 'Reserved',
                  '2026-08-29T00:00:00Z', '2026-08-29T02:00:00Z',
                  '2026-08-29T00:00:00Z', 1);

             INSERT INTO quota_usage (
                 tenant_id, committed_uploads, committed_bytes,
                 committed_objects, committed_compute_units, committed_jobs,
                 committed_budget_units, reserved_uploads, reserved_bytes,
                 reserved_objects, reserved_compute_units, reserved_jobs,
                 reserved_budget_units, version)
             VALUES (
                 '{TenantId:D}', 0, 0, 0, 0, 0, 0, 1, 22, 2, 2, 0, 0, 1);
             """);
    }

    private static string SharedMemoryConnectionString() =>
        $"Data Source=migration-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = NormalizeSqliteGuids(commandText);
        await command.ExecuteNonQueryAsync();
    }

    private static string NormalizeSqliteGuids(string commandText) =>
        commandText
            .Replace(
                TenantId.ToString("D"),
                TenantId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                FirstActorId.ToString("D"),
                FirstActorId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                SecondActorId.ToString("D"),
                SecondActorId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                MultipartUploadId.ToString("D"),
                MultipartUploadId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                DirectUploadId.ToString("D"),
                DirectUploadId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                MultipartReservationId.ToString("D"),
                MultipartReservationId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                DirectReservationId.ToString("D"),
                DirectReservationId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                StandaloneReservationId.ToString("D"),
                StandaloneReservationId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                AuthSessionId.ToString("D"),
                AuthSessionId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal)
            .Replace(
                ApiKeyId.ToString("D"),
                ApiKeyId.ToString("D").ToUpperInvariant(),
                StringComparison.Ordinal);

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = NormalizeSqliteGuids(commandText);
        return (T)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The query returned no value."));
    }

    private sealed class MutableTenantScope : IMutableTenantScope
    {
        public Guid TenantId { get; private set; }

        public void Establish(Guid tenantId)
        {
            if (TenantId != Guid.Empty && TenantId != tenantId)
            {
                throw new InvalidOperationException(
                    "The test tenant scope cannot switch tenants.");
            }

            TenantId = tenantId;
        }
    }

    private sealed class FixedUuid7Generator : IUuid7Generator
    {
        public Guid NewId() =>
            Guid.Parse("01991a54-6c00-7000-8000-0000000001ff");
    }
}
