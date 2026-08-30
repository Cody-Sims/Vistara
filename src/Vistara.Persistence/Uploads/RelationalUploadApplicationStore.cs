using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Application.Uploads.Quotas;
using Vistara.Domain.Jobs;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Jobs;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Uploads;

public sealed class RelationalUploadApplicationStore
{
    private const int ReservedJobsPerUpload = 5;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly VistaraDbContext _context;
    private readonly IBlobStore _blobStore;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly UploadPersistenceOptions _options;

    public RelationalUploadApplicationStore(
        VistaraDbContext context,
        IBlobStore blobStore,
        IClock clock,
        IUuid7Generator idGenerator,
        UploadPersistenceOptions options)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MaximumUploadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            options.MultipartThresholdBytes);
        if (options.PlanLifetime < TimeSpan.FromMinutes(5) ||
            options.PlanLifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Upload plan lifetime must be between five and ten minutes.");
        }

        if (options.OutcomeReconciliationGrace <= TimeSpan.Zero ||
            options.OutcomeReconciliationGrace > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Upload outcome reconciliation grace must be positive and no more than seven days.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.StorageContainer);
    }

    public async ValueTask<long> GetMaximumUploadBytesAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        TenantRow tenant = await _context.Tenants
            .AsNoTracking()
            .SingleAsync(row => row.Id == (TenantKey)tenantId, cancellationToken);
        long maximum = ReadQuotaPolicy(tenant.QuotasJson).MaximumUploadBytes
            ?? _options.MaximumUploadBytes;
        await transaction.CommitAsync(cancellationToken);
        return maximum;
    }

    public async ValueTask<PersistedUploadReserveResult> ReserveAsync(
        PersistedUploadReserveCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateReserveCommand(command);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        try
        {
            TenantKey tenantId = command.TenantId;
            IdempotencyRequestRow? existingRequest =
                await _context.IdempotencyRequests.SingleOrDefaultAsync(
                    row =>
                        row.TenantId == tenantId &&
                        row.PrincipalId == command.ActorId &&
                        row.Key == command.IdempotencyKey,
                    cancellationToken);
            if (existingRequest is not null)
            {
                PersistedUploadReserveResult replay =
                    await ReplayReservationAsync(
                        command,
                        existingRequest,
                        cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return replay;
            }

            TenantRow? tenant = await _context.Tenants
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.Id == tenantId,
                    cancellationToken);
            if (tenant is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(
                    PersistedUploadReserveStatus.Unavailable,
                    null);
            }

            QuotaPolicy quotaPolicy = ReadQuotaPolicy(tenant.QuotasJson);
            QuotaUsageSnapshot usage =
                await QuotaPersistence.ReadSnapshotAsync(
                    _context,
                    cancellationToken);
            Guid reservationId = _idGenerator.NewId();
            QuotaStoreReserveResult quota =
                await QuotaPersistence.TryReserveTrackedAsync(
                    _context,
                    new AtomicQuotaReservation(
                        new TenantId(command.TenantId),
                        reservationId,
                        UploadQuotaKey(command),
                        command.RequestHash,
                        new QuotaAmounts(
                            uploads: 1,
                            bytes: command.ExpectedSizeBytes,
                            objects: 1,
                            transformations: 0,
                            jobs: ReservedJobsPerUpload,
                            budgetUnits: 0),
                        quotaPolicy.Limits,
                        usage.Version,
                        EnsureUtc(_clock.UtcNow),
                        command.ExpiresAtUtc),
                    command.UploadId,
                    cancellationToken);
            if (quota.Status == QuotaStoreReserveStatus.LimitExceeded)
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return new(
                    PersistedUploadReserveStatus.QuotaExceeded,
                    null);
            }

            if (quota.Status != QuotaStoreReserveStatus.Reserved)
            {
                await transaction.RollbackAsync(cancellationToken);
                _context.ChangeTracker.Clear();
                return new(
                    PersistedUploadReserveStatus.Unavailable,
                    null);
            }

            DateTimeOffset now = EnsureUtc(_clock.UtcNow);
            var row = new UploadSessionRow
            {
                Id = command.UploadId,
                TenantId = command.TenantId,
                ActorId = command.ActorId,
                DisplayFileName = command.DisplayFileName,
                Strategy = ToStoredStrategy(command.Strategy),
                StagingKey = command.StagingKey,
                StorageProvider = _blobStore.Name,
                StorageContainer = _options.StorageContainer,
                ExpectedBytes = command.ExpectedSizeBytes,
                ExpectedSha256 = command.Sha256,
                DeclaredContentType = command.DeclaredContentType,
                State = "Pending",
                ExpiresAtUtc = command.ExpiresAtUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            };
            _context.UploadSessions.Add(row);
            _context.IdempotencyRequests.Add(new IdempotencyRequestRow
            {
                TenantId = command.TenantId,
                PrincipalId = command.ActorId,
                Key = command.IdempotencyKey,
                RequestHash = command.RequestHash,
                UploadSessionId = command.UploadId,
                ResponseReference = command.UploadId.ToString("D"),
                ExpiresAtUtc = command.ExpiresAtUtc,
            });
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadSession session = await SnapshotAsync(
                command.TenantId,
                command.UploadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(PersistedUploadReserveStatus.Created, session);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReplayAfterConflictAsync(command, cancellationToken);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            _context.ChangeTracker.Clear();
            return await ReplayAfterConflictAsync(command, cancellationToken);
        }
    }

    public async ValueTask<PersistedUploadIssuance> IssueAsync(
        PersistedUploadSession supplied,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(
                supplied.Strategy,
                "multipart",
                StringComparison.Ordinal) &&
            _blobStore is not IDurableMultipartBlobStore)
        {
            throw new InvalidOperationException(
                "The storage provider cannot issue recoverable multipart uploads.");
        }

        UploadSessionRow observed = await PrepareIssuanceAsync(
            supplied,
            cancellationToken);

        DirectUploadPlan? directPlan = null;
        MultipartSession? multipart = null;
        MultipartPartPlan[] parts = [];
        BlobMetadata metadata = RequiredMetadata(observed);
        try
        {
            switch (observed.Strategy)
            {
                case "Proxy":
                    break;
                case "Direct":
                    directPlan = await _blobStore.CreateDirectUploadAsync(
                        new DirectUploadRequest(
                            new BlobKey(observed.StagingKey),
                            observed.ExpectedBytes,
                            new BlobMediaType(observed.DeclaredContentType),
                            NativeSha256(observed),
                            BlobRequestConditions.CreateOnly,
                            _options.PlanLifetime,
                            metadata),
                        cancellationToken);
                    break;
                case "Multipart":
                    DateTimeOffset now = EnsureUtc(_clock.UtcNow);
                    TimeSpan remaining = observed.ExpiresAtUtc - now;
                    TimeSpan partPlanLifetime = TimeSpan.FromTicks(
                        observed.MultipartPartPlanLifetimeTicks
                            ?? throw new InvalidOperationException(
                                "The durable multipart issuance is incomplete."));
                    multipart = observed.ProviderUploadId is null
                        ? await DurableMultipartStore().GetOrCreateMultipartAsync(
                            MultipartIssuanceId(observed)
                                ?? throw new InvalidOperationException(
                                    "The multipart issuance ID is missing."),
                            new MultipartRequest(
                                new BlobKey(observed.StagingKey),
                                observed.ExpectedBytes,
                                new BlobMediaType(observed.DeclaredContentType),
                                checksum: null,
                                BlobRequestConditions.CreateOnly,
                                remaining,
                                partPlanLifetime,
                                metadata),
                            cancellationToken)
                        : RestoreMultipart(observed);
                    ValidateMultipartSession(observed, multipart, now);
                    MultipartPartPlan initialPlan =
                        await _blobStore.CreatePartPlanAsync(
                            multipart,
                            1,
                            cancellationToken);
                    ValidatePartPlan(multipart, initialPlan, 1, now);
                    parts =
                    [
                        initialPlan,
                    ];
                    break;
                default:
                    throw new InvalidOperationException(
                        "The persisted upload strategy is invalid.");
            }
        }
        catch (BlobStoreException exception)
        {
            throw new InvalidOperationException(
                "The storage provider could not issue an upload plan.",
                exception);
        }

        if (observed.State == "Pending" && observed.Strategy == "Multipart")
        {
            multipart = await CompleteMultipartIssuanceAsync(
                supplied,
                observed,
                multipart ?? throw new InvalidOperationException(
                    "A multipart issuance is missing its provider session."),
                cancellationToken);
        }
        else if (observed.State == "Pending")
        {
            _context.ChangeTracker.Clear();
            await using IDbContextTransaction transaction =
                await TenantDatabaseTransaction.BeginAsync(
                    _context,
                    supplied.TenantId,
                    cancellationToken);
            UploadSessionRow row = await LoadTrackedAsync(
                supplied.UploadId,
                cancellationToken);
            EnsureSnapshotMatches(supplied, row);
            if (row.State != "Pending")
            {
                throw new DbUpdateConcurrencyException(
                    "The upload was issued by another request.");
            }

            row.State = "UploadIssued";
            row.ProviderUploadId = multipart?.UploadId;
            row.MultipartProviderState = multipart?.ProviderState;
            row.MultipartExpiresAtUtc = multipart?.ExpiresAtUtc;
            row.MultipartPartPlanLifetimeTicks =
                multipart?.PartPlanLifetime.Ticks;
            row.MultipartMaxParts = multipart?.MaxParts;
            row.MultipartMinPartBytes = multipart?.MinPartBytes;
            row.MultipartMaxPartBytes = multipart?.MaxPartBytes;
            row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            row.Version = checked(row.Version + 1);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                await transaction.RollbackAsync(cancellationToken);
                if (multipart is not null && observed.ProviderUploadId is null)
                {
                    try
                    {
                        await _blobStore.AbortMultipartAsync(
                            multipart,
                            CancellationToken.None);
                    }
                    catch (BlobStoreException)
                    {
                    }
                }

                throw new InvalidOperationException(
                    "The upload changed while its provider plan was issued.",
                    exception);
            }
        }

        PersistedUploadSession session = await GetAsync(
            supplied.TenantId,
            supplied.UploadId,
            cancellationToken) ?? throw new InvalidOperationException(
            "The issued upload session disappeared.");
        return new PersistedUploadIssuance(session, directPlan, multipart, parts);
    }

    private async ValueTask<UploadSessionRow> PrepareIssuanceAsync(
        PersistedUploadSession supplied,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _context.ChangeTracker.Clear();
            await using IDbContextTransaction transaction =
                await TenantDatabaseTransaction.BeginAsync(
                    _context,
                    supplied.TenantId,
                    cancellationToken);
            UploadSessionRow row = await LoadTrackedAsync(
                supplied.UploadId,
                cancellationToken);
            EnsureUploadIdentityMatches(supplied, row);
            if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The upload session has expired.");
            }

            if (row.State is not ("Pending" or "UploadIssued"))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "Only pending or issued uploads can receive an upload plan.");
            }

            if (row.Strategy != "Multipart")
            {
                EnsureSnapshotMatches(supplied, row);
                await transaction.CommitAsync(cancellationToken);
                _context.Entry(row).State = EntityState.Detached;
                return row;
            }

            if (supplied.Version > row.Version ||
                (supplied.Version != row.Version &&
                 MultipartIssuanceId(row) is null &&
                 row.State != "UploadIssued"))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The upload snapshot does not match persisted state.");
            }

            if (row.State == "UploadIssued" ||
                MultipartIssuanceId(row) is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                _context.Entry(row).State = EntityState.Detached;
                return row;
            }

            long expectedVersion = row.Version;
            row.MultipartProviderState =
                $"issuance:v1:mpi-{_idGenerator.NewId():N}";
            TimeSpan remaining = row.ExpiresAtUtc - EnsureUtc(_clock.UtcNow);
            row.MultipartPartPlanLifetimeTicks =
                (remaining < _options.PlanLifetime
                    ? remaining
                    : _options.PlanLifetime).Ticks;
            row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            row.Version = checked(row.Version + 1);
            _context.Entry(row).Property(item => item.Version).OriginalValue =
                expectedVersion;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _context.Entry(row).State = EntityState.Detached;
                return row;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (DbException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "The multipart issuance could not be durably prepared.");
    }

    private async ValueTask<MultipartSession> CompleteMultipartIssuanceAsync(
        PersistedUploadSession supplied,
        UploadSessionRow prepared,
        MultipartSession multipart,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            _context.ChangeTracker.Clear();
            await using IDbContextTransaction transaction =
                await TenantDatabaseTransaction.BeginAsync(
                    _context,
                    supplied.TenantId,
                    cancellationToken);
            UploadSessionRow row = await LoadTrackedAsync(
                supplied.UploadId,
                cancellationToken);
            EnsureUploadIdentityMatches(supplied, row);
            if (row.State == "UploadIssued")
            {
                MultipartSession persisted = RestoreMultipart(row);
                await transaction.RollbackAsync(cancellationToken);
                if (persisted.UploadId != multipart.UploadId ||
                    persisted.ProviderState != multipart.ProviderState)
                {
                    throw new InvalidOperationException(
                        "Concurrent multipart issuance returned a different provider session.");
                }

                return persisted;
            }

            if (row.State != "Pending" ||
                !string.Equals(
                    MultipartIssuanceId(row),
                    MultipartIssuanceId(prepared),
                    StringComparison.Ordinal) ||
                row.Version != prepared.Version)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "The upload changed while its provider plan was issued.");
            }

            long expectedVersion = row.Version;
            row.State = "UploadIssued";
            row.ProviderUploadId = multipart.UploadId;
            row.MultipartProviderState = multipart.ProviderState;
            row.MultipartExpiresAtUtc = multipart.ExpiresAtUtc;
            row.MultipartPartPlanLifetimeTicks =
                multipart.PartPlanLifetime.Ticks;
            row.MultipartMaxParts = multipart.MaxParts;
            row.MultipartMinPartBytes = multipart.MinPartBytes;
            row.MultipartMaxPartBytes = multipart.MaxPartBytes;
            row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            row.Version = checked(row.Version + 1);
            _context.Entry(row).Property(item => item.Version).OriginalValue =
                expectedVersion;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return multipart;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (DbException)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "The multipart provider plan could not be durably recorded.");
    }

    public async ValueTask<PersistedUploadSession?> GetAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow? row = await _context.UploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == uploadId,
                cancellationToken);
        PersistedUploadSession? session = row is null
            ? null
            : await SnapshotAsync(row, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async ValueTask<PersistedUploadWriteResult> WriteProxyAsync(
        PersistedUploadSession supplied,
        Stream content,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        _context.ChangeTracker.Clear();
        UploadSessionRow observed;
        await using (IDbContextTransaction readTransaction =
                     await TenantDatabaseTransaction.BeginAsync(
                         _context,
                         supplied.TenantId,
                         cancellationToken))
        {
            observed = await _context.UploadSessions
                .AsNoTracking()
                .SingleAsync(
                    row => row.Id == supplied.UploadId,
                    cancellationToken);
            EnsureSnapshotMatches(supplied, observed);
            PersistedUploadWriteStatus? invalid = ValidateWrite(
                observed,
                expectedVersion,
                "Proxy");
            if (invalid.HasValue)
            {
                await readTransaction.RollbackAsync(cancellationToken);
                return new(invalid.Value, null);
            }

            await readTransaction.CommitAsync(cancellationToken);
        }

        BlobHead head;
        var boundedContent = new BoundedProxyReadStream(
            content,
            observed.ExpectedBytes);
        try
        {
            BlobChecksum? checksum = NativeSha256(observed);
            BlobWriteResult result = await _blobStore.PutAsync(
                new BlobKey(observed.StagingKey),
                new SingleUseStreamContent(
                    boundedContent,
                    observed.ExpectedBytes),
                new BlobWriteOptions(
                    new BlobMediaType(observed.DeclaredContentType),
                    RequiredMetadata(observed),
                    checksum is null ? [] : [checksum],
                    BlobRequestConditions.CreateOnly),
                cancellationToken);
            head = result.Head;
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.IntegrityMismatch)
        {
            return new(PersistedUploadWriteStatus.IntegrityMismatch, null);
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.InvalidRequest)
        {
            return new(PersistedUploadWriteStatus.TooLarge, null);
        }
        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.PreconditionFailed)
        {
            BlobHead? existing = await _blobStore.HeadAsync(
                new BlobKey(observed.StagingKey),
                cancellationToken);
            if (existing is null || !Matches(observed, existing))
            {
                return new(PersistedUploadWriteStatus.IntegrityMismatch, null);
            }

            head = existing;
        }
        catch (BlobStoreException)
        {
            return new(PersistedUploadWriteStatus.Unavailable, null);
        }

        if (!Matches(observed, head))
        {
            return new(PersistedUploadWriteStatus.IntegrityMismatch, null);
        }

        PersistedUploadWriteStatus? bodyStatus =
            await boundedContent.DrainAndValidateAsync(cancellationToken);
        if (bodyStatus.HasValue)
        {
            return new(bodyStatus.Value, null);
        }

        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                supplied.TenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(
            supplied.UploadId,
            cancellationToken);
        PersistedUploadWriteStatus? writeInvalid = ValidateWrite(
            row,
            expectedVersion,
            "Proxy");
        if (writeInvalid.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(writeInvalid.Value, null);
        }

        CaptureStagingIdentity(row, head);
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadSession updated = await SnapshotAsync(
                supplied.TenantId,
                supplied.UploadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadWriteStatus.Written,
                updated);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadWriteStatus.VersionConflict, null);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadWriteStatus.VersionConflict, null);
        }
    }

    public async ValueTask<PersistedUploadPartPlanResult> RefreshPartPlansAsync(
        PersistedUploadSession supplied,
        IReadOnlyList<int> partNumbers,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(partNumbers);
        _context.ChangeTracker.Clear();
        MultipartSession session;
        await using (IDbContextTransaction transaction =
                     await TenantDatabaseTransaction.BeginAsync(
                         _context,
                         supplied.TenantId,
                         cancellationToken))
        {
            UploadSessionRow row = await _context.UploadSessions
                .AsNoTracking()
                .SingleAsync(
                    candidate => candidate.Id == supplied.UploadId,
                    cancellationToken);
            EnsureUploadIdentityMatches(supplied, row);
            PersistedUploadPartPlanStatus? invalid = ValidatePartPlan(
                row,
                expectedVersion);
            if (invalid.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(invalid.Value, []);
            }

            session = RestoreMultipart(row);
            await transaction.CommitAsync(cancellationToken);
        }

        var plans = new MultipartPartPlan[partNumbers.Count];
        try
        {
            for (int index = 0; index < partNumbers.Count; index++)
            {
                plans[index] = await _blobStore.CreatePartPlanAsync(
                    session,
                    partNumbers[index],
                    cancellationToken);
                ValidatePartPlan(
                    session,
                    plans[index],
                    partNumbers[index],
                    EnsureUtc(_clock.UtcNow));
            }
        }
        catch (BlobStoreException)
        {
            return new(PersistedUploadPartPlanStatus.Unavailable, []);
        }

        return new(PersistedUploadPartPlanStatus.Created, plans);
    }

    public async ValueTask<PersistedUploadCommitResult> CommitAsync(
        PersistedUploadSession supplied,
        IReadOnlyList<PersistedCommittedUploadPart> parts,
        string idempotencyKey,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(idempotencyKey));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string requestHash = CommitHash(parts);
        UploadSessionRow observed;
        long completionVersion = expectedVersion;
        IReadOnlyList<PersistedCommittedUploadPart> completionParts = parts;
        if (string.Equals(supplied.Strategy, "multipart", StringComparison.Ordinal))
        {
            MultipartPartsVerification verification =
                await VerifyMultipartPartsAsync(
                    supplied,
                    parts,
                    idempotencyKey,
                    requestHash,
                    expectedVersion,
                    cancellationToken);
            if (verification.Failure is not null)
            {
                return verification.Failure;
            }

            completionParts = verification.Parts;
            MultipartCommitPreparation preparation =
                await PrepareMultipartCommitAsync(
                    supplied,
                    verification.Parts,
                    idempotencyKey,
                    requestHash,
                    expectedVersion,
                    cancellationToken);
            if (preparation.Failure is not null)
            {
                return preparation.Failure;
            }

            observed = preparation.Row
                ?? throw new InvalidOperationException(
                    "The prepared multipart upload is missing.");
            completionVersion = observed.Version;
        }
        else
        {
            _context.ChangeTracker.Clear();
            await using IDbContextTransaction readTransaction =
                await TenantDatabaseTransaction.BeginAsync(
                    _context,
                    supplied.TenantId,
                    cancellationToken);
            observed = await LoadTrackedAsync(supplied.UploadId, cancellationToken);
            EnsureUploadIdentityMatches(supplied, observed);
            PersistedUploadCommitResult? existing =
                await ExistingCommitAsync(
                    observed,
                    idempotencyKey,
                    requestHash,
                    cancellationToken);
            if (existing is not null)
            {
                await readTransaction.RollbackAsync(cancellationToken);
                return existing;
            }

            if (observed.Version != expectedVersion)
            {
                await readTransaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadCommitStatus.VersionConflict, null);
            }

            if (EnsureUtc(_clock.UtcNow) >= observed.ExpiresAtUtc)
            {
                await ExpireUploadAsync(observed, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);
                await readTransaction.CommitAsync(cancellationToken);
                return new(PersistedUploadCommitStatus.Expired, null);
            }

            if (observed.State != "UploadIssued")
            {
                await readTransaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadCommitStatus.InvalidState, null);
            }

            await readTransaction.CommitAsync(cancellationToken);
        }

        BlobHead? head;
        try
        {
            head = await _blobStore.HeadAsync(
                new BlobKey(observed.StagingKey),
                cancellationToken);
            if (head is null && observed.Strategy == "Multipart")
            {
                MultipartCompletion completion =
                    await _blobStore.CompleteMultipartAsync(
                        RestoreMultipart(observed),
                        completionParts.Select(ToUploadedPart).ToArray(),
                        cancellationToken);
                head = completion.Head;
            }
        }

        catch (BlobStoreException exception)
            when (exception.Code == BlobStoreErrorCode.OutcomeUnknown)
        {
            return observed.Strategy == "Multipart"
                ? await RecordCommitOutcomeUnknownAsync(
                    supplied,
                    idempotencyKey,
                    requestHash,
                    completionVersion,
                    cancellationToken)
                : new(PersistedUploadCommitStatus.Unavailable, null);
        }
        catch (BlobStoreException)
        {
            return new(PersistedUploadCommitStatus.Unavailable, null);
        }

        if (head is null || !Matches(observed, head))
        {
            return new(PersistedUploadCommitStatus.InvalidState, null);
        }

        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                supplied.TenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(
            supplied.UploadId,
            cancellationToken);
        if (row.Version != completionVersion)
        {
            PersistedUploadCommitResult? concurrent =
                await ExistingCommitAsync(
                    row,
                    idempotencyKey,
                    requestHash,
                    cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return concurrent ??
                new(PersistedUploadCommitStatus.VersionConflict, null);
        }

        if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
        {
            await ExpireUploadAsync(row, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.Expired, null);
        }

        string requiredState = row.Strategy == "Multipart"
            ? "Committing"
            : "UploadIssued";
        if (row.State != requiredState ||
            (row.Strategy == "Multipart" &&
             (!string.Equals(
                  row.CommitIdempotencyKey,
                  idempotencyKey,
                  StringComparison.Ordinal) ||
              !string.Equals(
                  row.CommitRequestHash,
                  requestHash,
                  StringComparison.Ordinal))))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.InvalidState, null);
        }

        CaptureStagingIdentity(row, head);
        row.CommitIdempotencyKey = idempotencyKey;
        row.CommitRequestHash = requestHash;
        await ExtendReservationForProcessingAsync(
            row,
            EnsureUtc(_clock.UtcNow),
            cancellationToken);
        row.LastKnownState = requiredState;
        row.State = "CommitRequested";
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            completionVersion;
        _context.Jobs.Add(JobMapper.ToRow(CreateIngestJob(row)));
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadSession updated = await SnapshotAsync(
                supplied.TenantId,
                supplied.UploadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                PersistedUploadCommitStatus.Queued,
                updated);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.VersionConflict, null);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.Unavailable, null);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.VersionConflict, null);
        }
    }

    private async ValueTask<MultipartPartsVerification> VerifyMultipartPartsAsync(
        PersistedUploadSession supplied,
        IReadOnlyList<PersistedCommittedUploadPart> claimedParts,
        string idempotencyKey,
        string requestHash,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        MultipartSession session;
        await using (IDbContextTransaction transaction =
                     await TenantDatabaseTransaction.BeginAsync(
                         _context,
                         supplied.TenantId,
                         cancellationToken))
        {
            UploadSessionRow row = await LoadTrackedAsync(
                supplied.UploadId,
                cancellationToken);
            EnsureUploadIdentityMatches(supplied, row);
            PersistedUploadCommitResult? existing = await ExistingCommitAsync(
                row,
                idempotencyKey,
                requestHash,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(existing, []);
            }

            if (row.Version != expectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(
                    new PersistedUploadCommitResult(
                        PersistedUploadCommitStatus.VersionConflict,
                        null),
                    []);
            }

            if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(
                    new PersistedUploadCommitResult(
                        PersistedUploadCommitStatus.Expired,
                        null),
                    []);
            }

            if (row.State != "UploadIssued" ||
                row.Strategy != "Multipart" ||
                row.ProviderUploadId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(
                    new PersistedUploadCommitResult(
                        PersistedUploadCommitStatus.InvalidState,
                        null),
                    []);
            }

            session = RestoreMultipart(row);
            await transaction.RollbackAsync(cancellationToken);
        }

        if (claimedParts.Count == 0 ||
            claimedParts.Count > session.MaxParts)
        {
            return new(
                new PersistedUploadCommitResult(
                    PersistedUploadCommitStatus.InvalidState,
                    null),
                []);
        }

        MultipartInventory inventory;
        try
        {
            inventory = await DurableMultipartStore().InspectMultipartAsync(
                session,
                claimedParts.Select(ToUploadedPart).ToArray(),
                cancellationToken);
        }
        catch (BlobStoreException)
        {
            return new(
                new PersistedUploadCommitResult(
                    PersistedUploadCommitStatus.Unavailable,
                    null),
                []);
        }

        if (inventory.State is not (
                MultipartInventoryState.Active or
                MultipartInventoryState.Completed) ||
            !TryValidateProviderParts(
                session,
                claimedParts,
                inventory.Parts,
                out PersistedCommittedUploadPart[] verified))
        {
            return new(
                new PersistedUploadCommitResult(
                    PersistedUploadCommitStatus.InvalidState,
                    null),
                []);
        }

        return new(null, verified);
    }

    private async ValueTask<MultipartCommitPreparation> PrepareMultipartCommitAsync(
        PersistedUploadSession supplied,
        IReadOnlyList<PersistedCommittedUploadPart> parts,
        string idempotencyKey,
        string requestHash,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                supplied.TenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(
            supplied.UploadId,
            cancellationToken);
        EnsureUploadIdentityMatches(supplied, row);
        PersistedUploadCommitResult? existing = await ExistingCommitAsync(
            row,
            idempotencyKey,
            requestHash,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, existing);
        }

        if (row.Version != expectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.VersionConflict, null));
        }

        if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
        {
            await ExpireUploadAsync(row, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.Expired, null));
        }

        if (row.State != "UploadIssued" ||
            row.Strategy != "Multipart" ||
            row.ProviderUploadId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.InvalidState, null));
        }

        _context.UploadParts.AddRange(parts.Select(part => new UploadPartRow
        {
            TenantId = row.TenantId,
            UploadSessionId = row.Id,
            PartNumber = part.PartNumber,
            EntityTag = part.EntityTag,
            Checksum = part.Checksum,
            SizeBytes = part.SizeBytes,
        }));
        row.LastKnownState = row.State;
        row.State = "Committing";
        row.CommitIdempotencyKey = idempotencyKey;
        row.CommitRequestHash = requestHash;
        await ExtendReservationForProcessingAsync(
            row,
            EnsureUtc(_clock.UtcNow),
            cancellationToken);
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _context.Entry(row).State = EntityState.Detached;
            return new(row, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.VersionConflict, null));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.Unavailable, null));
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(
                null,
                new(PersistedUploadCommitStatus.VersionConflict, null));
        }
    }

    private async ValueTask<PersistedUploadCommitResult?>
        ExistingCommitAsync(
            UploadSessionRow row,
            string idempotencyKey,
            string requestHash,
            CancellationToken cancellationToken)
    {
        if (row.CommitIdempotencyKey is not null)
        {
            bool same = string.Equals(
                    row.CommitIdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    row.CommitRequestHash,
                    requestHash,
                    StringComparison.Ordinal);
            PersistedUploadCommitStatus status = same
                ? row.State == "Accepted"
                    ? PersistedUploadCommitStatus.AlreadyAccepted
                    : PersistedUploadCommitStatus.Replayed
                : PersistedUploadCommitStatus.IdempotencyConflict;
            PersistedUploadSession? replay = same
                ? await SnapshotAsync(row, cancellationToken)
                : null;
            return new(status, replay);
        }

        if (row.State == "Accepted")
        {
            return new(
                PersistedUploadCommitStatus.AlreadyAccepted,
                await SnapshotAsync(row, cancellationToken));
        }

        return null;
    }

    private async ValueTask<PersistedUploadCommitResult>
        RecordCommitOutcomeUnknownAsync(
            PersistedUploadSession supplied,
            string idempotencyKey,
            string requestHash,
            long expectedVersion,
            CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                supplied.TenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(
            supplied.UploadId,
            cancellationToken);
        bool sameRequest = string.Equals(
                row.CommitIdempotencyKey,
                idempotencyKey,
                StringComparison.Ordinal) &&
            string.Equals(
                row.CommitRequestHash,
                requestHash,
                StringComparison.Ordinal);
        if (row.State == "OutcomeUnknown" &&
            row.LastKnownState == "Committing" &&
            sameRequest)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.OutcomeUnknown, null);
        }

        if (row.Version != expectedVersion ||
            row.State != "Committing" ||
            !sameRequest)
        {
            PersistedUploadCommitResult? existing = await ExistingCommitAsync(
                row,
                idempotencyKey,
                requestHash,
                cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return existing ??
                new(PersistedUploadCommitStatus.VersionConflict, null);
        }

        row.LastKnownState = "Committing";
        row.State = "OutcomeUnknown";
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.OutcomeUnknown, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.VersionConflict, null);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadCommitStatus.VersionConflict, null);
        }
    }

    public async ValueTask<PersistedUploadAbortResult> AbortAsync(
        PersistedUploadSession supplied,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(supplied.Strategy, "multipart", StringComparison.Ordinal))
        {
            return await AbortMultipartAsync(
                supplied,
                expectedVersion,
                cancellationToken);
        }

        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                supplied.TenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(
            supplied.UploadId,
            cancellationToken);
        if (row.Version != expectedVersion)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }

        if (row.State == "Aborted")
        {
            PersistedUploadSession aborted =
                await SnapshotAsync(row, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.AlreadyAborted, aborted);
        }

        if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc || row.State == "Expired")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.Expired, null);
        }

        if (row.State is not ("Pending" or "UploadIssued"))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.InvalidState, null);
        }

        row.State = "Aborted";
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadSession updated = await SnapshotAsync(
                supplied.TenantId,
                supplied.UploadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await CleanupAbortedUploadAsync(row, cancellationToken);
            return new(
                PersistedUploadAbortStatus.Aborted,
                updated);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }
    }

    private async ValueTask<PersistedUploadAbortResult> AbortMultipartAsync(
        PersistedUploadSession supplied,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        UploadSessionRow prepared;
        _context.ChangeTracker.Clear();
        await using (IDbContextTransaction transaction =
                     await TenantDatabaseTransaction.BeginAsync(
                         _context,
                         supplied.TenantId,
                         cancellationToken))
        {
            UploadSessionRow row = await LoadTrackedAsync(
                supplied.UploadId,
                cancellationToken);
            EnsureUploadIdentityMatches(supplied, row);
            if (row.Version != expectedVersion)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.VersionConflict, null);
            }

            if (row.State == "Aborted")
            {
                PersistedUploadSession aborted =
                    await SnapshotAsync(row, cancellationToken);
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.AlreadyAborted, aborted);
            }

            if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc ||
                row.State == "Expired")
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.Expired, null);
            }

            if (row.State == "OutcomeUnknown" &&
                row.LastKnownState == "Aborting")
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.Unavailable, null);
            }

            if (row.State is not ("Pending" or "UploadIssued"))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.InvalidState, null);
            }

            if (row.ProviderUploadId is null)
            {
                if (MultipartIssuanceId(row) is not null)
                {
                    row.LastKnownState = row.State;
                    row.State = "Aborting";
                    await ExtendReservationForProcessingAsync(
                        row,
                        EnsureUtc(_clock.UtcNow),
                        cancellationToken);
                    row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
                    row.Version = checked(row.Version + 1);
                    _context.Entry(row).Property(item => item.Version).OriginalValue =
                        expectedVersion;
                    try
                    {
                        await _context.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new(PersistedUploadAbortStatus.Unavailable, null);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new(PersistedUploadAbortStatus.VersionConflict, null);
                    }
                    catch (DbException)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return new(PersistedUploadAbortStatus.VersionConflict, null);
                    }
                }

                row.LastKnownState = row.State;
                row.State = "Aborted";
                row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
                row.Version = checked(row.Version + 1);
                _context.Entry(row).Property(item => item.Version).OriginalValue =
                    expectedVersion;
                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                    PersistedUploadSession updated = await SnapshotAsync(
                        supplied.TenantId,
                        supplied.UploadId,
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new(PersistedUploadAbortStatus.Aborted, updated);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(PersistedUploadAbortStatus.VersionConflict, null);
                }
                catch (DbException)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(PersistedUploadAbortStatus.VersionConflict, null);
                }
            }

            row.LastKnownState = row.State;
            row.State = "Aborting";
            await ExtendReservationForProcessingAsync(
                row,
                EnsureUtc(_clock.UtcNow),
                cancellationToken);
            row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            row.Version = checked(row.Version + 1);
            _context.Entry(row).Property(item => item.Version).OriginalValue =
                expectedVersion;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                _context.Entry(row).State = EntityState.Detached;
                prepared = row;
            }
            catch (DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.VersionConflict, null);
            }
            catch (DbException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(PersistedUploadAbortStatus.VersionConflict, null);
            }
        }

        try
        {
            await _blobStore.AbortMultipartAsync(
                RestoreMultipart(prepared),
                cancellationToken);
        }
        catch (BlobStoreException)
        {
            await RecordAbortOutcomeUnknownAsync(
                supplied.TenantId,
                supplied.UploadId,
                prepared.Version,
                cancellationToken);
            return new(PersistedUploadAbortStatus.Unavailable, null);
        }

        return await CompletePreparedAbortAsync(
            supplied.TenantId,
            supplied.UploadId,
            prepared.Version,
            cancellationToken);
    }

    private async ValueTask RecordAbortOutcomeUnknownAsync(
        Guid tenantId,
        Guid uploadId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(uploadId, cancellationToken);
        if (row.Version == expectedVersion && row.State == "Aborting")
        {
            row.LastKnownState = "Aborting";
            row.State = "OutcomeUnknown";
            row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
            row.Version = checked(row.Version + 1);
            _context.Entry(row).Property(item => item.Version).OriginalValue =
                expectedVersion;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
            }
            catch (DbException)
            {
            }
        }

        await transaction.RollbackAsync(CancellationToken.None);
    }

    private async ValueTask<PersistedUploadAbortResult> CompletePreparedAbortAsync(
        Guid tenantId,
        Guid uploadId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        _context.ChangeTracker.Clear();
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                tenantId,
                cancellationToken);
        UploadSessionRow row = await LoadTrackedAsync(uploadId, cancellationToken);
        if (row.State == "Aborted")
        {
            PersistedUploadSession replay =
                await SnapshotAsync(row, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.AlreadyAborted, replay);
        }

        if (row.Version != expectedVersion || row.State != "Aborting")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }

        row.LastKnownState = "Aborting";
        row.State = "Aborted";
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
        _context.Entry(row).Property(item => item.Version).OriginalValue =
            expectedVersion;
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            PersistedUploadSession updated = await SnapshotAsync(
                tenantId,
                uploadId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.Aborted, updated);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }
        catch (DbException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(PersistedUploadAbortStatus.VersionConflict, null);
        }
    }

    private async ValueTask CleanupAbortedUploadAsync(
        UploadSessionRow row,
        CancellationToken cancellationToken)
    {
        try
        {
            if (row.Strategy == "Multipart" && row.ProviderUploadId is not null)
            {
                await _blobStore.AbortMultipartAsync(
                    RestoreMultipart(row),
                    cancellationToken);
                return;
            }

            BlobHead? head = await _blobStore.HeadAsync(
                new BlobKey(row.StagingKey),
                cancellationToken);
            if (head is not null)
            {
                _ = await _blobStore.DeleteAsync(
                    new BlobKey(row.StagingKey),
                    new BlobDeleteOptions(
                        new BlobRequestConditions(ifMatch: head.Identity.Version)),
                    cancellationToken);
            }
        }
        catch (BlobStoreException)
        {
        }
    }

    private async ValueTask<PersistedUploadReserveResult> ReplayReservationAsync(
        PersistedUploadReserveCommand command,
        IdempotencyRequestRow request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                request.RequestHash,
                command.RequestHash,
                StringComparison.Ordinal) ||
            request.UploadSessionId is null)
        {
            return new(PersistedUploadReserveStatus.IdempotencyConflict, null);
        }

        UploadSessionRow? row = await _context.UploadSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == request.UploadSessionId.Value,
                cancellationToken);
        return row is null
            ? new(PersistedUploadReserveStatus.Unavailable, null)
            : new(
                PersistedUploadReserveStatus.Replayed,
                await SnapshotAsync(row, cancellationToken));
    }

    private async ValueTask<PersistedUploadReserveResult> ReplayAfterConflictAsync(
        PersistedUploadReserveCommand command,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await TenantDatabaseTransaction.BeginAsync(
                _context,
                command.TenantId,
                cancellationToken);
        IdempotencyRequestRow? existing = await _context.IdempotencyRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.PrincipalId == command.ActorId &&
                    row.Key == command.IdempotencyKey,
                cancellationToken);
        PersistedUploadReserveResult result = existing is null
            ? new(PersistedUploadReserveStatus.Unavailable, null)
            : await ReplayReservationAsync(command, existing, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return result;
    }

    private async ValueTask<PersistedUploadSession> SnapshotAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        TenantKey key = tenantId;
        UploadSessionRow row = await _context.UploadSessions
            .AsNoTracking()
            .SingleAsync(
                candidate =>
                    candidate.TenantId == key &&
                    candidate.Id == uploadId,
                cancellationToken);
        return await SnapshotAsync(row, cancellationToken);
    }

    private async ValueTask<PersistedUploadSession> SnapshotAsync(
        UploadSessionRow row,
        CancellationToken cancellationToken)
    {
        PersistedUploadPart[] parts = await _context.UploadParts
            .AsNoTracking()
            .Where(candidate => candidate.UploadSessionId == row.Id)
            .OrderBy(candidate => candidate.PartNumber)
            .Select(candidate => new PersistedUploadPart(
                candidate.PartNumber,
                candidate.SizeBytes,
                candidate.Checksum))
            .ToArrayAsync(cancellationToken);
        return new PersistedUploadSession(
            row.TenantId,
            row.ActorId,
            row.Id,
            ToExternalValue(row.Strategy),
            ToExternalValue(row.State),
            row.ExpectedBytes,
            row.DeclaredContentType,
            row.ExpectedSha256,
            row.DisplayFileName,
            row.StagingKey,
            row.ExpiresAtUtc,
            row.Version,
            parts);
    }

    private Task<UploadSessionRow> LoadTrackedAsync(
        Guid uploadId,
        CancellationToken cancellationToken) =>
        _context.UploadSessions.SingleAsync(
            row => row.Id == uploadId,
            cancellationToken);

    private PersistedUploadWriteStatus? ValidateWrite(
        UploadSessionRow row,
        long expectedVersion,
        string requiredStrategy)
    {
        if (row.Version != expectedVersion)
        {
            return PersistedUploadWriteStatus.VersionConflict;
        }

        if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
        {
            return PersistedUploadWriteStatus.Expired;
        }

        return row.State == "UploadIssued" && row.Strategy == requiredStrategy
            ? null
            : PersistedUploadWriteStatus.InvalidState;
    }

    private PersistedUploadPartPlanStatus? ValidatePartPlan(
        UploadSessionRow row,
        long expectedVersion)
    {
        if (row.Version != expectedVersion)
        {
            return PersistedUploadPartPlanStatus.VersionConflict;
        }

        if (EnsureUtc(_clock.UtcNow) >= row.ExpiresAtUtc)
        {
            return PersistedUploadPartPlanStatus.Expired;
        }

        return row.State == "UploadIssued" &&
            row.Strategy == "Multipart" &&
            row.ProviderUploadId is not null
                ? null
                : PersistedUploadPartPlanStatus.InvalidState;
    }

    private async ValueTask ExpireUploadAsync(
        UploadSessionRow row,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        row.State = "Expired";
        row.UpdatedAtUtc = EnsureUtc(_clock.UtcNow);
        row.Version = checked(row.Version + 1);
    }

    private async ValueTask ExtendReservationForProcessingAsync(
        UploadSessionRow row,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        QuotaReservationRow reservation =
            await _context.QuotaReservations.SingleAsync(
                candidate => candidate.UploadSessionId == row.Id,
                cancellationToken);
        if (reservation.State != "Reserved")
        {
            throw new InvalidOperationException(
                "Only a reserved upload quota can enter provider reconciliation.");
        }

        DateTimeOffset extendedExpiry =
            nowUtc + _options.OutcomeReconciliationGrace;
        if (reservation.ExpiresAtUtc >= extendedExpiry)
        {
            return;
        }

        reservation.ExpiresAtUtc = extendedExpiry;
        reservation.UpdatedAtUtc = nowUtc;
        reservation.Version = checked(reservation.Version + 1);
    }

    private DurableJob CreateIngestJob(UploadSessionRow row)
    {
        DateTimeOffset now = EnsureUtc(_clock.UtcNow);
        return DurableJob.Create(
            new JobId(_idGenerator.NewId()),
            new JobTenantId(row.TenantId),
            new JobType("upload.ingest"),
            JsonSerializer.Serialize(
                new UploadIngestPayload(row.Id),
                JsonOptions),
            payloadVersion: 1,
            new JobDedupeKey($"upload:{row.Id:D}:ingest:v1"),
            priority: 0,
            maxAttempts: 5,
            availableAtUtc: now,
            createdAtUtc: now);
    }

    private static UploadedPart ToUploadedPart(PersistedCommittedUploadPart part) =>
        new(
            part.PartNumber,
            new BlobEntityTag(part.EntityTag),
            part.Checksum is null
                ? null
                : new BlobChecksum(
                    BlobChecksumAlgorithm.Sha256,
                    part.Checksum),
            part.SizeBytes);

    private static bool TryValidateProviderParts(
        MultipartSession session,
        IReadOnlyList<PersistedCommittedUploadPart> claimed,
        IReadOnlyList<UploadedPart> observed,
        out PersistedCommittedUploadPart[] verified)
    {
        verified = [];
        if (observed.Count == 0 ||
            observed.Count != claimed.Count ||
            observed.Count > session.MaxParts)
        {
            return false;
        }

        var result = new PersistedCommittedUploadPart[observed.Count];
        long total = 0;
        for (int index = 0; index < observed.Count; index++)
        {
            UploadedPart actual = observed[index];
            PersistedCommittedUploadPart reported = claimed[index];
            BlobChecksum? actualSha256 =
                actual.Checksum?.Algorithm == BlobChecksumAlgorithm.Sha256
                    ? actual.Checksum
                    : null;
            if (actual.PartNumber != index + 1 ||
                actual.PartNumber != reported.PartNumber ||
                actual.EntityTag.Value != reported.EntityTag ||
                actual.SizeBytes > session.MaxPartBytes ||
                (index < observed.Count - 1 &&
                 actual.SizeBytes < session.MinPartBytes) ||
                (actualSha256 is not null &&
                 reported.Checksum is not null &&
                 !string.Equals(
                     actualSha256.Value,
                     reported.Checksum,
                     StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                total = checked(total + actual.SizeBytes);
            }
            catch (OverflowException)
            {
                return false;
            }
            result[index] = new PersistedCommittedUploadPart(
                actual.PartNumber,
                actual.EntityTag.Value,
                actualSha256?.Value,
                actual.SizeBytes);
        }

        if (total != session.ContentLength)
        {
            return false;
        }

        verified = result;
        return true;
    }

    private static void ValidateMultipartSession(
        UploadSessionRow row,
        MultipartSession session,
        DateTimeOffset utcNow)
    {
        if (session.Key.Value != row.StagingKey ||
            session.ContentLength != row.ExpectedBytes ||
            session.ContentType.Value != row.DeclaredContentType ||
            session.CompletionConditions != BlobRequestConditions.CreateOnly ||
            session.Checksum is not null ||
            session.ExpiresAtUtc <= utcNow ||
            session.ExpiresAtUtc > row.ExpiresAtUtc + TimeSpan.FromMinutes(1) ||
            session.PartPlanLifetime.Ticks !=
                row.MultipartPartPlanLifetimeTicks ||
            !HasMetadata(
                session.Metadata,
                "vistara-tenant-id",
                row.TenantId.Value.ToString("D")) ||
            !HasMetadata(
                session.Metadata,
                "vistara-upload-id",
                row.Id.ToString("D")) ||
            (MultipartIssuanceId(row) is { } issuanceId &&
             !HasMetadata(
                 session.Metadata,
                 "vistara-multipart-issuance-id",
                 issuanceId)))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The provider returned an invalid multipart session.");
        }
    }

    private static void ValidatePartPlan(
        MultipartSession session,
        MultipartPartPlan plan,
        int requestedPartNumber,
        DateTimeOffset utcNow)
    {
        if (plan.UploadId != session.UploadId ||
            plan.PartNumber != requestedPartNumber ||
            plan.MinBytes != session.MinPartBytes ||
            plan.MaxBytes != session.MaxPartBytes ||
            plan.ExpiresAtUtc <= utcNow ||
            plan.ExpiresAtUtc > session.ExpiresAtUtc)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The provider returned an invalid multipart part plan.");
        }
    }

    private static bool HasMetadata(
        BlobMetadata metadata,
        string key,
        string expected) =>
        metadata.TryGetValue(key, out string? value) &&
        string.Equals(value, expected, StringComparison.Ordinal);

    private BlobChecksum? NativeSha256(UploadSessionRow row) =>
        _blobStore.Capabilities.NativeChecksumAlgorithms.Contains(
            BlobChecksumAlgorithm.Sha256)
            ? new BlobChecksum(
                BlobChecksumAlgorithm.Sha256,
                row.ExpectedSha256)
            : null;

    private IDurableMultipartBlobStore DurableMultipartStore() =>
        _blobStore as IDurableMultipartBlobStore ??
        throw new BlobStoreException(
            BlobStoreErrorCode.Unsupported,
            "The storage provider does not expose durable multipart issuance and inventory.");

    private static string CommitHash(
        IReadOnlyList<PersistedCommittedUploadPart> parts)
    {
        var canonical = new StringBuilder();
        foreach (PersistedCommittedUploadPart part in parts)
        {
            canonical
                .Append(part.PartNumber.ToString(CultureInfo.InvariantCulture))
                .Append('\n')
                .Append(part.EntityTag)
                .Append('\n')
                .Append(part.Checksum)
                .Append('\n')
                .Append(part.SizeBytes.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void CaptureStagingIdentity(
        UploadSessionRow row,
        BlobHead head)
    {
        row.StagingProviderVersion = head.Identity.Version.Value;
        row.StagingEntityTag = head.Properties.EntityTag.Value;
        row.StagingProviderChecksum = head.Properties.Checksums
            .FirstOrDefault(checksum =>
                checksum.Algorithm == BlobChecksumAlgorithm.Sha256)
            ?.Value;
    }

    private static bool Matches(UploadSessionRow row, BlobHead head)
    {
        if (head.Identity.Key.Value != row.StagingKey ||
            head.Properties.ContentLength != row.ExpectedBytes ||
            !string.Equals(
                head.Properties.ContentType.Value,
                row.DeclaredContentType,
                StringComparison.Ordinal) ||
            !head.Properties.Metadata.TryGetValue(
                "vistara-tenant-id",
                out string? tenantId) ||
            !string.Equals(
                tenantId,
                row.TenantId.Value.ToString("D"),
                StringComparison.Ordinal) ||
            !head.Properties.Metadata.TryGetValue(
                "vistara-upload-id",
                out string? uploadId) ||
            !string.Equals(
                uploadId,
                row.Id.ToString("D"),
                StringComparison.Ordinal))
        {
            return false;
        }

        BlobChecksum? checksum = head.Properties.Checksums.SingleOrDefault(
            candidate => candidate.Algorithm == BlobChecksumAlgorithm.Sha256);
        return checksum is null ||
            string.Equals(
                checksum.Value,
                row.ExpectedSha256,
                StringComparison.Ordinal);
    }

    private static BlobMetadata RequiredMetadata(UploadSessionRow row)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vistara-tenant-id"] = row.TenantId.Value.ToString("D"),
            ["vistara-upload-id"] = row.Id.ToString("D"),
        };
        if (MultipartIssuanceId(row) is { } issuanceId)
        {
            metadata["vistara-multipart-issuance-id"] =
                issuanceId;
        }

        return new BlobMetadata(metadata);
    }

    private static string? MultipartIssuanceId(UploadSessionRow row)
    {
        const string prefix = "issuance:v1:";
        return row.ProviderUploadId is null &&
            row.MultipartProviderState is { } state &&
            state.StartsWith(prefix, StringComparison.Ordinal) &&
            state.Length > prefix.Length
                ? state[prefix.Length..]
                : null;
    }

    private static MultipartSession RestoreMultipart(UploadSessionRow row)
    {
        if (row.ProviderUploadId is null ||
        row.MultipartProviderState is null ||
        row.MultipartExpiresAtUtc is null ||
        row.MultipartPartPlanLifetimeTicks is null ||
        row.MultipartMaxParts is null ||
            row.MultipartMinPartBytes is null ||
            row.MultipartMaxPartBytes is null)
        {
            throw new InvalidOperationException(
                "The persisted multipart session is incomplete.");
        }

        return new MultipartSession(
            row.ProviderUploadId,
            new BlobKey(row.StagingKey),
            row.MultipartExpiresAtUtc.Value,
            row.ExpectedBytes,
            BlobRequestConditions.CreateOnly,
            row.MultipartMaxParts.Value,
            row.MultipartMinPartBytes.Value,
            row.MultipartMaxPartBytes.Value,
            TimeSpan.FromTicks(row.MultipartPartPlanLifetimeTicks.Value),
            new BlobMediaType(row.DeclaredContentType),
            checksum: null,
            RequiredMetadata(row),
            row.MultipartProviderState);
    }

    private static void EnsureSnapshotMatches(
        PersistedUploadSession supplied,
        UploadSessionRow row)
    {
        EnsureUploadIdentityMatches(supplied, row);
        if (supplied.Version != row.Version)
        {
            throw new InvalidOperationException(
                "The upload snapshot does not match persisted state.");
        }
    }

    private static void EnsureUploadIdentityMatches(
        PersistedUploadSession supplied,
        UploadSessionRow row)
    {
        if (supplied.TenantId != row.TenantId.Value ||
            supplied.ActorId != row.ActorId ||
            supplied.UploadId != row.Id ||
            !string.Equals(
                supplied.Strategy,
                ToExternalValue(row.Strategy),
                StringComparison.Ordinal) ||
            !string.Equals(
                supplied.StagingKey,
                row.StagingKey,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The upload snapshot does not match persisted state.");
        }
    }

    private static string ToStoredStrategy(string strategy) =>
        strategy switch
        {
            "proxy" => "Proxy",
            "direct" => "Direct",
            "multipart" => "Multipart",
            _ => throw new ArgumentOutOfRangeException(
                nameof(strategy),
                strategy,
                "The upload strategy is invalid."),
        };

    private static string ToExternalValue(string value) =>
        value.Length == 0
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static string UploadQuotaKey(PersistedUploadReserveCommand command) =>
        $"upload:{command.ActorId:N}:{command.IdempotencyKey}";

    private static void ValidateReserveCommand(
        PersistedUploadReserveCommand command)
    {
        EnsureUuid7(command.TenantId, nameof(command.TenantId));
        EnsureUuid7(command.ActorId, nameof(command.ActorId));
        EnsureUuid7(command.UploadId, nameof(command.UploadId));
        _ = new BlobKey(command.StagingKey);
        _ = new BlobMediaType(command.DeclaredContentType);
        _ = new BlobChecksum(BlobChecksumAlgorithm.Sha256, command.Sha256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            command.ExpectedSizeBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayFileName);
        if (command.DisplayFileName.Length > 255 ||
            command.DisplayFileName.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "The display file name is invalid.");
        }

        if (command.IdempotencyKey.Length is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                "The idempotency key is invalid.");
        }

        _ = new Vistara.Domain.Assets.Sha256Checksum(command.RequestHash);
        if (command.ExpiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Upload expiry must use UTC.",
                nameof(command));
        }

        _ = ToStoredStrategy(command.Strategy);
    }

    private static DateTimeOffset EnsureUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("The persistence clock must return UTC.");
        }

        return value;
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException(
                "Upload identifiers must be UUIDv7.",
                parameterName);
        }
    }

    private static QuotaPolicy ReadQuotaPolicy(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return QuotaPolicy.Unlimited;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return new QuotaPolicy(
                ReadPositive(root, "maximumUploadBytes") ??
                    ReadPositive(root, "maxUploadBytes"),
                new QuotaLimits(
                    Limit(root, "concurrentUploads"),
                    Limit(root, "pendingUploadBytes", "storedBytes"),
                    Limit(root, "activeObjects", "objects"),
                    Limit(root, "transformations"),
                    Limit(root, "queuedJobs", "jobs"),
                    Limit(root, "budgetUnits")));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "The tenant quota policy is invalid.");
        }
    }

    private static QuotaLimit Limit(
        JsonElement root,
        string primary,
        string? fallback = null)
    {
        long? value = ReadNonNegative(root, primary) ??
            (fallback is null ? null : ReadNonNegative(root, fallback));
        return value.HasValue
            ? QuotaLimit.Limited(value.Value)
            : QuotaLimit.Unlimited;
    }

    private static long? ReadPositive(JsonElement root, string name)
    {
        long? value = ReadOptionalLong(root, name);
        if (value is <= 0)
        {
            throw new InvalidOperationException(
                $"Tenant quota '{name}' must be positive.");
        }

        return value;
    }

    private static long? ReadNonNegative(JsonElement root, string name)
    {
        long? value = ReadOptionalLong(root, name);
        if (value < 0)
        {
            throw new InvalidOperationException(
                $"Tenant quota '{name}' cannot be negative.");
        }

        return value;
    }

    private static long? ReadOptionalLong(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out JsonElement property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out long value))
        {
            throw new InvalidOperationException(
                $"Tenant quota '{name}' must be an integer.");
        }

        return value;
    }

    private sealed record QuotaPolicy(
        long? MaximumUploadBytes,
        QuotaLimits Limits)
    {
        internal static QuotaPolicy Unlimited { get; } = new(
            null,
            new QuotaLimits(
                QuotaLimit.Unlimited,
                QuotaLimit.Unlimited,
                QuotaLimit.Unlimited,
                QuotaLimit.Unlimited,
                QuotaLimit.Unlimited,
                QuotaLimit.Unlimited));
    }

    private sealed class SingleUseStreamContent(Stream stream, long length)
        : IReplayableBlobContent
    {
        private int _opened;

        public long Length { get; } = length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _opened, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The proxy request stream cannot be replayed.");
            }

            return ValueTask.FromResult(stream);
        }
    }

    private sealed class BoundedProxyReadStream(
        Stream inner,
        long expectedLength) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException(
                "Only asynchronous proxy upload reads are supported.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_bytesRead >= expectedLength)
            {
                return 0;
            }

            int permitted = checked((int)Math.Min(
                buffer.Length,
                expectedLength - _bytesRead));
            int read = await inner.ReadAsync(
                buffer[..permitted],
                cancellationToken);
            _bytesRead = checked(_bytesRead + read);
            return read;
        }

        internal async ValueTask<PersistedUploadWriteStatus?>
            DrainAndValidateAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[8 * 1024];
            while (_bytesRead < expectedLength)
            {
                int read = await ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return PersistedUploadWriteStatus.IntegrityMismatch;
                }
            }

            byte[] overflowProbe = new byte[1];
            return await inner.ReadAsync(
                overflowProbe,
                cancellationToken) == 0
                ? null
                : PersistedUploadWriteStatus.TooLarge;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => base.DisposeAsync();
    }

    private sealed record MultipartCommitPreparation(
        UploadSessionRow? Row,
        PersistedUploadCommitResult? Failure);

    private sealed record MultipartPartsVerification(
        PersistedUploadCommitResult? Failure,
        IReadOnlyList<PersistedCommittedUploadPart> Parts);

    private sealed record UploadIngestPayload(Guid UploadSessionId);
}
