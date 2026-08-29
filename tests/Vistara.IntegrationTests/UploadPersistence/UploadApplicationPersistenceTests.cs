using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;
using Xunit;

namespace Vistara.IntegrationTests.UploadPersistence;

public sealed class UploadApplicationPersistenceTests
{
    private const string Sha256 =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Create_issue_status_commit_and_replay_survive_new_scopes()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);

        UploadSessionSnapshot issued;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                Request(tenantId, actorId, uploadId, "create-key", "request-a"),
                CancellationToken.None);
            Assert.Equal(UploadReserveStatus.Created, reserved.Status);
            Assert.Equal("pending", reserved.Session?.State);
            Assert.Equal("../../display-name.jpg", reserved.Session?.DisplayFileName);

            UploadIssuance issuance = await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None);
            issued = issuance.Session;
            Assert.Equal("uploadIssued", issued.State);
            Assert.Equal(2, issued.Version);
            Assert.NotNull(issuance.DirectRequest);
        }

        storage.StoreUploaded(storage.LastDirectRequest!);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadSessionSnapshot persisted = Assert.IsType<UploadSessionSnapshot>(
                await application.GetAsync(tenantId, uploadId, CancellationToken.None));
            Assert.Equal(issued, persisted);

            UploadCommitResult committed = await application.CommitAsync(
                persisted,
                [],
                new IdempotencyKey("commit-key"),
                persisted.Version,
                CancellationToken.None);
            Assert.Equal(UploadCommitStatus.Queued, committed.Status);
            Assert.Equal("commitRequested", committed.Session?.State);
            Assert.Equal(3, committed.Session?.Version);

            UploadCommitResult replay = await application.CommitAsync(
                committed.Session!,
                [],
                new IdempotencyKey("commit-key"),
                committed.Session!.Version,
                CancellationToken.None);
            Assert.Equal(UploadCommitStatus.Replayed, replay.Status);
        }

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await context.UploadSessions.SingleAsync();
        Assert.Equal("../../display-name.jpg", row.DisplayFileName);
        Assert.Equal("test-provider", row.StorageProvider);
        Assert.Equal("provider-v1", row.StagingProviderVersion);
        Assert.Equal(
            UploadPersistenceDatabase.Now.AddHours(24),
            await context.QuotaReservations
                .Select(item => item.ExpiresAtUtc)
                .SingleAsync());
        Assert.Equal(
            5,
            await context.QuotaReservations
                .Select(item => item.ReservedJobs)
                .SingleAsync());
        Assert.Equal(
            5,
            await context.QuotaUsage
                .Select(item => item.ReservedJobs)
                .SingleAsync());
        Assert.DoesNotContain("test-secret", string.Join(
            "|",
            row.GetType()
                .GetProperties()
                .Where(property => property.PropertyType == typeof(string))
                .Select(property => property.GetValue(row) as string ?? string.Empty)),
            StringComparison.Ordinal);
        Assert.Equal(1, await database.CountAsync("jobs"));
    }

    [Fact]
    public async Task Create_idempotency_replays_matching_request_and_rejects_mismatch()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        using ServiceProvider provider =
            database.CreateApiProvider(tenantId, new TestBlobStore());

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult created = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2)),
                "stable-key",
                "request-a"),
            CancellationToken.None);
        UploadReserveResult replay = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(3)),
                "stable-key",
                "request-a"),
            CancellationToken.None);
        UploadReserveResult conflict = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(4)),
                "stable-key",
                "request-b"),
            CancellationToken.None);

        Assert.Equal(UploadReserveStatus.Created, created.Status);
        Assert.Equal(UploadReserveStatus.Replayed, replay.Status);
        Assert.Equal(created.Session?.UploadId, replay.Session?.UploadId);
        Assert.Equal(UploadReserveStatus.IdempotencyConflict, conflict.Status);
        Assert.Equal(1, await database.CountAsync("upload_sessions"));
        Assert.Equal(1, await database.CountAsync("quota_reservations"));
    }

    [Fact]
    public async Task Upload_reservation_rejects_a_zero_queued_job_quota_atomically()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(
            tenantId,
            actorId,
            """{"queuedJobs":0}""");
        using ServiceProvider provider =
            database.CreateApiProvider(tenantId, new TestBlobStore());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();

        UploadReserveResult result = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                Guid.CreateVersion7(
                    UploadPersistenceDatabase.Now.AddMilliseconds(2)),
                "zero-jobs",
                "zero-jobs-request"),
            CancellationToken.None);

        Assert.Equal(UploadReserveStatus.QuotaExceeded, result.Status);
        Assert.Equal(0, await database.CountAsync("upload_sessions"));
        Assert.Equal(0, await database.CountAsync("quota_reservations"));
        Assert.Equal(0, await database.CountAsync("jobs"));
    }

    [Fact]
    public async Task Proxy_write_and_abort_are_persistent_versioned_and_release_quota()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        byte[] bytes = "persistent-proxy-upload"u8.ToArray();
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        using ServiceProvider provider =
            database.CreateApiProvider(tenantId, new TestBlobStore(direct: false));

        UploadSessionSnapshot stale;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                Request(
                    tenantId,
                    actorId,
                    uploadId,
                    "proxy-create",
                    "proxy-request",
                    sha,
                    bytes.LongLength,
                    "proxy"),
                CancellationToken.None);
            UploadIssuance issued = await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None);
            stale = issued.Session;
            UploadWriteResult written = await application.WriteProxyAsync(
                issued.Session,
                new MemoryStream(bytes, writable: false),
                issued.Session.Version,
                CancellationToken.None);
            Assert.Equal(UploadWriteStatus.Written, written.Status);
            Assert.Equal(3, written.Session?.Version);

            UploadAbortResult aborted = await application.AbortAsync(
                written.Session!,
                written.Session!.Version,
                CancellationToken.None);
            Assert.Equal(UploadAbortStatus.Aborted, aborted.Status);
            Assert.Equal("aborted", aborted.Session?.State);

            UploadAbortResult conflict = await application.AbortAsync(
                stale,
                stale.Version,
                CancellationToken.None);
            Assert.Equal(UploadAbortStatus.VersionConflict, conflict.Status);
        }

        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Assert.Equal(
            "Released",
            await context.QuotaReservations.Select(row => row.State).SingleAsync());
        Vistara.Persistence.Uploads.QuotaUsageRow usage =
            await context.QuotaUsage.SingleAsync();
        Assert.Equal(0, usage.ReservedJobs);
        Assert.Equal(0, usage.CommittedJobs);
    }

    [Fact]
    public async Task Proxy_streaming_does_not_hold_a_database_transaction()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        byte[] bytes = "stream-without-database-lock"u8.ToArray();
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new(direct: false);
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        UploadSessionSnapshot issued;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                Request(
                    tenantId,
                    actorId,
                    uploadId,
                    "stream-create",
                    "stream-request",
                    sha,
                    bytes.LongLength,
                    "proxy"),
                CancellationToken.None);
            issued = (await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None)).Session;
            storage.BeforePutAsync = async cancellationToken =>
            {
                await using Vistara.Persistence.VistaraDbContext concurrent =
                    database.CreateContext(tenantId);
                Vistara.Persistence.Model.TenantRow tenant =
                    await concurrent.Tenants.SingleAsync(cancellationToken);
                tenant.Name = "Updated while streaming";
                tenant.Version++;
                await concurrent.SaveChangesAsync(cancellationToken);
            };

            UploadWriteResult written = await application.WriteProxyAsync(
                issued,
                new MemoryStream(bytes, writable: false),
                issued.Version,
                CancellationToken.None);
            Assert.Equal(UploadWriteStatus.Written, written.Status);
        }
    }

    [Fact]
    public async Task Proxy_replay_validates_the_bounded_body_before_advancing_persisted_state()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        byte[] expected = "bounded-replay"u8.ToArray();
        string sha = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(expected));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new(direct: false)
        {
            RejectCreateBeforeRead = true,
        };
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult reserved = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                uploadId,
                "proxy-replay-create",
                "proxy-replay-request",
                sha,
                expected.LongLength,
                "proxy"),
            CancellationToken.None);
        UploadSessionSnapshot issued = (await application.IssueAsync(
            reserved.Session!,
            CancellationToken.None)).Session;
        storage.StoreUploaded(
            new DirectUploadRequest(
                new BlobKey(issued.StagingKey),
                issued.ExpectedSizeBytes,
                new BlobMediaType(issued.DeclaredContentType),
                new BlobChecksum(BlobChecksumAlgorithm.Sha256, issued.Sha256),
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(5),
                new BlobMetadata(
                [
                    new("vistara-tenant-id", tenantId.ToString("D")),
                    new("vistara-upload-id", uploadId.ToString("D")),
                ])),
            expected);
        byte[] oversized = [.. expected, 0xff];
        var body = new MemoryStream(oversized, writable: false);

        UploadWriteResult result = await application.WriteProxyAsync(
            issued,
            body,
            issued.Version,
            CancellationToken.None);

        Assert.Equal(UploadWriteStatus.TooLarge, result.Status);
        Assert.Equal(body.Length, body.Position);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await context.UploadSessions.SingleAsync();
        Assert.Equal(issued.Version, row.Version);
        Assert.Null(row.StagingProviderVersion);
    }

    [Fact]
    public async Task Commit_storage_verification_does_not_hold_a_database_transaction()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        UploadSessionSnapshot issued;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                Request(
                    tenantId,
                    actorId,
                    uploadId,
                    "commit-create",
                    "commit-request"),
                CancellationToken.None);
            issued = (await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None)).Session;
            storage.StoreUploaded(storage.LastDirectRequest!);
            storage.BeforeHeadAsync = async cancellationToken =>
            {
                await using Vistara.Persistence.VistaraDbContext concurrent =
                    database.CreateContext(tenantId);
                Vistara.Persistence.Model.TenantRow tenant =
                    await concurrent.Tenants.SingleAsync(cancellationToken);
                tenant.Name = "Updated while verifying";
                tenant.Version++;
                await concurrent.SaveChangesAsync(cancellationToken);
            };

            UploadCommitResult committed = await application.CommitAsync(
                issued,
                [],
                new IdempotencyKey("commit-key"),
                issued.Version,
                CancellationToken.None);
            Assert.Equal(UploadCommitStatus.Queued, committed.Status);
        }
    }

    [Fact]
    public async Task Signed_plan_creation_does_not_hold_a_database_transaction()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult reserved = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                uploadId,
                "plan-create",
                "plan-request"),
            CancellationToken.None);
        storage.BeforeDirectPlanAsync = async cancellationToken =>
        {
            await using Vistara.Persistence.VistaraDbContext concurrent =
                database.CreateContext(tenantId);
            Vistara.Persistence.Model.TenantRow tenant =
                await concurrent.Tenants.SingleAsync(cancellationToken);
            tenant.Name = "Updated while signing";
            tenant.Version++;
            await concurrent.SaveChangesAsync(cancellationToken);
        };

        UploadIssuance issued = await application.IssueAsync(
            reserved.Session!,
            CancellationToken.None);

        Assert.Equal("uploadIssued", issued.Session.State);
    }

    [Fact]
    public async Task Multipart_provider_identity_and_parts_survive_adapter_restart()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new();

        UploadSessionSnapshot issued;
        DateTimeOffset initialPartExpiry;
        using (ServiceProvider first = database.CreateApiProvider(tenantId, storage))
        {
            await using AsyncServiceScope scope = first.CreateAsyncScope();
            IUploadApplicationPort application =
                scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
            UploadReserveResult reserved = await application.ReserveAsync(
                Request(
                    tenantId,
                    actorId,
                    uploadId,
                    "multipart-create",
                    "multipart-request",
                    sizeBytes: 20_000_000,
                    strategy: "multipart"),
                CancellationToken.None);
            UploadIssuance issuance = await application.IssueAsync(
                reserved.Session!,
                CancellationToken.None);
            issued = issuance.Session;
            initialPartExpiry = Assert.Single(issuance.Parts).Request.ExpiresAtUtc;
        }

        TestBlobStore replica = storage.CreateReplica(
            UploadPersistenceDatabase.Now.AddMinutes(2));
        using ServiceProvider restarted =
            database.CreateApiProvider(tenantId, replica);
        await using AsyncServiceScope restartedScope = restarted.CreateAsyncScope();
        IUploadApplicationPort restartedApplication =
            restartedScope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadSessionSnapshot persisted = Assert.IsType<UploadSessionSnapshot>(
            await restartedApplication.GetAsync(
                tenantId,
                uploadId,
                CancellationToken.None));
        UploadPartPlanResult refreshed =
            await restartedApplication.RefreshPartPlansAsync(
                persisted,
                [1],
                persisted.Version,
                CancellationToken.None);
        UploadCommitResult committed = await restartedApplication.CommitAsync(
            persisted,
            [new CommittedUploadPart(1, "etag-1", Sha256, 20_000_000)],
            new IdempotencyKey("multipart-commit"),
            persisted.Version,
            CancellationToken.None);

        Assert.Equal(UploadPartPlanStatus.Created, refreshed.Status);
        Assert.True(
            Assert.Single(refreshed.Parts).Request.ExpiresAtUtc >
            initialPartExpiry);
        Assert.Equal(UploadCommitStatus.Queued, committed.Status);
        await using Vistara.Persistence.VistaraDbContext context =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await context.UploadSessions.SingleAsync();
        Assert.Equal("multipart-", row.ProviderUploadId![..10]);
        Assert.StartsWith("test:v1:", row.MultipartProviderState);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, row.MultipartPartPlanLifetimeTicks);
        Assert.Single(await context.UploadParts.ToListAsync());
    }

    [Fact]
    public async Task Multipart_completion_inputs_are_durable_before_an_ambiguous_provider_result()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new()
        {
            CompleteOutcomeUnknownAfterStore = true,
        };
        storage.BeforeCompleteMultipartAsync = async cancellationToken =>
        {
            await using Vistara.Persistence.VistaraDbContext context =
                database.CreateContext(tenantId);
            Vistara.Persistence.Model.UploadSessionRow preparing =
                await context.UploadSessions.SingleAsync(cancellationToken);
            Assert.Equal("Committing", preparing.State);
            Assert.Equal("UploadIssued", preparing.LastKnownState);
            Assert.Single(await context.UploadParts.ToListAsync(cancellationToken));
        };
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult reserved = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                uploadId,
                "ambiguous-complete-create",
                "ambiguous-complete-request",
                sizeBytes: 20_000_000,
                strategy: "multipart"),
            CancellationToken.None);
        UploadSessionSnapshot issued = (await application.IssueAsync(
            reserved.Session!,
            CancellationToken.None)).Session;

        UploadCommitResult result = await application.CommitAsync(
            issued,
            [new CommittedUploadPart(1, "etag-1", Sha256, 20_000_000)],
            new IdempotencyKey("ambiguous-complete"),
            issued.Version,
            CancellationToken.None);

        Assert.Equal(UploadCommitStatus.OutcomeUnknown, result.Status);
        await using Vistara.Persistence.VistaraDbContext persisted =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await persisted.UploadSessions.SingleAsync();
        Assert.Equal("OutcomeUnknown", row.State);
        Assert.Equal("Committing", row.LastKnownState);
        Assert.Equal("ambiguous-complete", row.CommitIdempotencyKey);
        Assert.Single(await persisted.UploadParts.ToListAsync());
        Assert.Equal(
            UploadPersistenceDatabase.Now.AddHours(24),
            await persisted.QuotaReservations
                .Select(item => item.ExpiresAtUtc)
                .SingleAsync());
    }

    [Fact]
    public async Task Multipart_abort_input_and_quota_remain_durable_when_the_outcome_is_ambiguous()
    {
        Guid tenantId = Guid.CreateVersion7(UploadPersistenceDatabase.Now);
        Guid actorId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(1));
        Guid uploadId = Guid.CreateVersion7(UploadPersistenceDatabase.Now.AddMilliseconds(2));
        await using UploadPersistenceDatabase database =
            await UploadPersistenceDatabase.CreateAsync();
        await database.SeedTenantAsync(tenantId, actorId);
        TestBlobStore storage = new()
        {
            AbortOutcomeUnknown = true,
        };
        storage.BeforeAbortMultipartAsync = async cancellationToken =>
        {
            await using Vistara.Persistence.VistaraDbContext context =
                database.CreateContext(tenantId);
            Assert.Equal(
                "Aborting",
                await context.UploadSessions.Select(row => row.State)
                    .SingleAsync(cancellationToken));
            Assert.Equal(
                "Reserved",
                await context.QuotaReservations.Select(row => row.State)
                    .SingleAsync(cancellationToken));
        };
        using ServiceProvider provider = database.CreateApiProvider(tenantId, storage);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IUploadApplicationPort application =
            scope.ServiceProvider.GetRequiredService<IUploadApplicationPort>();
        UploadReserveResult reserved = await application.ReserveAsync(
            Request(
                tenantId,
                actorId,
                uploadId,
                "ambiguous-abort-create",
                "ambiguous-abort-request",
                sizeBytes: 20_000_000,
                strategy: "multipart"),
            CancellationToken.None);
        UploadSessionSnapshot issued = (await application.IssueAsync(
            reserved.Session!,
            CancellationToken.None)).Session;

        UploadAbortResult result = await application.AbortAsync(
            issued,
            issued.Version,
            CancellationToken.None);

        Assert.Equal(UploadAbortStatus.Unavailable, result.Status);
        await using Vistara.Persistence.VistaraDbContext persisted =
            database.CreateContext(tenantId);
        Vistara.Persistence.Model.UploadSessionRow row =
            await persisted.UploadSessions.SingleAsync();
        Assert.Equal("OutcomeUnknown", row.State);
        Assert.Equal("Aborting", row.LastKnownState);
        Assert.Equal(
            "Reserved",
            await persisted.QuotaReservations.Select(item => item.State).SingleAsync());
        Assert.Equal(
            UploadPersistenceDatabase.Now.AddHours(24),
            await persisted.QuotaReservations
                .Select(item => item.ExpiresAtUtc)
                .SingleAsync());
    }

    private static ReserveUploadRequest Request(
        Guid tenantId,
        Guid actorId,
        Guid uploadId,
        string idempotencyKey,
        string requestHash,
        string sha256 = Sha256,
        long sizeBytes = 1_000,
        string strategy = "direct") =>
        new(
            tenantId,
            actorId,
            uploadId,
            strategy,
            "../../display-name.jpg",
            sizeBytes,
            "image/jpeg",
            sha256,
            $"staging/{tenantId:N}/{uploadId:N}",
            Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(requestHash))),
            new IdempotencyKey(idempotencyKey),
            UploadPersistenceDatabase.Now.AddHours(1));
}
