using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Application.Common;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Persistence;
using Vistara.Persistence.Model;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Reconciliation.Uploads;

namespace Vistara.IntegrationTests.UploadPersistence;

internal sealed class UploadPersistenceDatabase : IAsyncDisposable
{
    internal static readonly DateTimeOffset Now =
        new(2036, 9, 10, 11, 12, 13, TimeSpan.Zero);

    private readonly SqliteConnection _anchor;

    private UploadPersistenceDatabase(
        SqliteConnection anchor,
        string connectionString)
    {
        _anchor = anchor;
        ConnectionString = connectionString;
    }

    internal string ConnectionString { get; }

    internal static async ValueTask<UploadPersistenceDatabase> CreateAsync()
    {
        string name = $"UploadPersistence-{Guid.NewGuid():N}";
        string connectionString = $"Data Source={name};Mode=Memory;Cache=Shared";
        var anchor = new SqliteConnection(connectionString);
        await anchor.OpenAsync();
        var database = new UploadPersistenceDatabase(anchor, connectionString);
        await using VistaraDbContext context =
            database.CreateContext(Guid.CreateVersion7(Now));
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return database;
    }

    internal async ValueTask SeedTenantAsync(
        Guid tenantId,
        Guid actorId,
        string quotasJson = "{}")
    {
        await using VistaraDbContext context = CreateContext(tenantId);
        context.Users.Add(new UserRow
        {
            Id = actorId,
            NormalizedEmail = $"{actorId:N}@example.test",
            DisplayName = "Upload owner",
            Status = "Active",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = $"tenant-{tenantId:N}",
            Name = "Upload tenant",
            Status = "Active",
            SettingsJson = "{}",
            QuotasJson = quotasJson,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    internal VistaraDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new VistaraDbContext(options, new FixedTenantScope(tenantId));
    }

    internal ServiceProvider CreateApiProvider(
        Guid tenantId,
        TestBlobStore blobStore)
    {
        ServiceCollection services = [];
        var tenantContext = new TestApiTenantContext(tenantId);
        services.AddScoped<ITenantScope>(_ => tenantContext);
        services.AddScoped<IPlatformTenantContext>(_ => tenantContext);
        services.AddSingleton<IBlobStore>(blobStore);
        services.AddSingleton<IClock>(new FixedClock(Now));
        services.AddSingleton<IUuid7Generator>(new SequenceUuid7Generator(Now));
        IConfiguration configuration = Configuration();
        services.AddVistaraApiPlatform(configuration);
        services.AddVistaraApiPersistence(configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
    }

    internal ServiceProvider CreateWorkerProvider(
        TestBlobStore blobStore,
        DateTimeOffset? now = null,
        bool addUploadReconciliation = false)
    {
        ServiceCollection services = [];
        services.AddSingleton<IBlobStore>(blobStore);
        services.AddSingleton<IImageProcessor>(NoopImageProcessor.Instance);
        services.AddSingleton<IClock>(new FixedClock(now ?? Now));
        services.AddSingleton<IUuid7Generator>(new SequenceUuid7Generator(Now));
        services.AddVistaraWorkerPlatform(Configuration(includeWorker: true));
        if (addUploadReconciliation)
        {
            services.AddVistaraUploadReconciliation();
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
            });
    }

    internal async ValueTask<long> CountAsync(string tableName)
    {
        await using SqliteCommand command = _anchor.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM \"{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private IConfiguration Configuration(bool includeWorker = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = ConnectionString,
        };
        if (includeWorker)
        {
            values["Worker:InstanceId"] = "upload-persistence-tests";
            values["Worker:Jobs:MaximumConcurrency"] = "1";
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();

    private sealed class TestApiTenantContext(Guid tenantId) :
        ITenantScope,
        IPlatformTenantContext
    {
        public Guid TenantId { get; } = tenantId;

        Guid? IPlatformTenantContext.TenantId => TenantId;
    }
}

internal sealed class FixedClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

internal sealed class SequenceUuid7Generator(DateTimeOffset timestamp) : IUuid7Generator
{
    private int _sequence;

    public Guid NewId() =>
        Guid.CreateVersion7(timestamp.AddMilliseconds(
            Interlocked.Increment(ref _sequence)));
}

internal sealed class NoopImageProcessor : IImageProcessor
{
    internal static NoopImageProcessor Instance { get; } = new();

    public ImageProcessorCapabilities Capabilities { get; } = new()
    {
        InputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
        MaxFrames = 1,
        StreamRequirements = new(false, false),
    };

    public ImagePipelineFingerprint PipelineFingerprint { get; } =
        new("upload-persistence-tests");

    public async ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await source.OpenReadAsync(cancellationToken);
        long bytes = 0;
        byte[] buffer = new byte[64];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            bytes = checked(bytes + read);
        }

        return new ImageInspection(
            ImageFormat.Jpeg,
            new ImageMediaType("image/jpeg"),
            640,
            480,
            1,
            307_200,
            ImagePixelFormat.Rgba8,
            ImageOrientation.Normal,
            new ImagePrivacyMetadata(
                HasExif: false,
                HasGps: false,
                HasXmp: false,
                HasIptc: false,
                HasComments: false,
                HasEmbeddedThumbnail: false,
                HasEmbeddedFileName: false),
            bytes,
            1_228_800);
    }

    public ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

internal sealed class TestBlobStore : IBlobStore
{
    private readonly Backend _backend;
    private readonly DateTimeOffset _now;

    internal TestBlobStore(
        bool direct = true,
        bool multipart = true)
        : this(new Backend(), UploadPersistenceDatabase.Now, direct, multipart)
    {
    }

    private TestBlobStore(
        Backend backend,
        DateTimeOffset now,
        bool direct,
        bool multipart)
    {
        _backend = backend;
        _now = now;
        Capabilities = new BlobStoreCapabilities
        {
            SupportsDirectUpload = direct,
            SupportsMultipartUpload = multipart,
            SupportsConditionalCreate = true,
            SupportsConditionalRead = true,
            SupportsConditionalDelete = true,
            SupportsConditionalCopy = true,
            SupportsConditionalMultipartCompletion = true,
            SupportsServerSideCopy = true,
            ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
            NativeChecksumAlgorithms = [BlobChecksumAlgorithm.Sha256],
            Limits = new BlobStoreLimits(
                50L * 1024 * 1024,
                1_024,
                10_000,
                1,
                50L * 1024 * 1024),
        };
    }

    public string Name => "test-provider";

    public BlobStoreCapabilities Capabilities { get; }

    internal DirectUploadRequest? LastDirectRequest { get; private set; }

    internal Func<CancellationToken, ValueTask>? BeforePutAsync { get; set; }

    internal Func<CancellationToken, ValueTask>? BeforeHeadAsync { get; set; }

    internal Func<CancellationToken, ValueTask>? BeforeDirectPlanAsync { get; set; }

    internal bool RejectCreateBeforeRead { get; init; }

    internal Func<CancellationToken, ValueTask>? BeforeCompleteMultipartAsync
    {
        get;
        set;
    }

    internal Func<CancellationToken, ValueTask>? BeforeAbortMultipartAsync
    {
        get;
        set;
    }

    internal bool CompleteOutcomeUnknownAfterStore { get; init; }

    internal bool AbortOutcomeUnknown { get; init; }

    internal bool Contains(BlobKey key) => _backend.Objects.ContainsKey(key);

    internal TestBlobStore CreateReplica(DateTimeOffset now) =>
        new(_backend, now, Capabilities.SupportsDirectUpload, Capabilities.SupportsMultipartUpload);

    internal void StoreUploaded(
        DirectUploadRequest request,
        byte[]? content = null)
    {
        BlobVersion version = NextVersion();
        byte[] bytes = content ??
            new byte[checked((int)request.ContentLength)];
        _backend.Objects[request.Key] = new StoredBlob(
            Head(
                request.Key,
                version,
                request.ContentLength,
                request.ContentType,
                request.Metadata,
                request.Checksum),
            bytes);
    }

    public ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken) =>
        HeadCoreAsync(key, cancellationToken);

    private async ValueTask<BlobHead?> HeadCoreAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeHeadAsync is not null)
        {
            await BeforeHeadAsync(cancellationToken);
        }

        _backend.Objects.TryGetValue(key, out StoredBlob? stored);
        return stored?.Head;
    }

    public ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_backend.Objects.TryGetValue(key, out StoredBlob? stored))
        {
            throw new BlobStoreException(BlobStoreErrorCode.NotFound, "Missing object.");
        }

        if (options.EffectiveConditions.IfMatch is { } version &&
            version != stored.Head.Identity.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The object changed.");
        }

        return ValueTask.FromResult(new BlobReadHandle(
            new MemoryStream(stored.Bytes, writable: false),
            stored.Head));
    }

    public async ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        if (BeforePutAsync is not null)
        {
            await BeforePutAsync(cancellationToken);
        }

        if (RejectCreateBeforeRead &&
            options.Conditions.RequireMissing &&
            _backend.Objects.ContainsKey(key))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The object already exists.");
        }

        await using Stream stream = await content.OpenReadAsync(cancellationToken);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[64];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }

        byte[] bytes = destination.ToArray();
        BlobChecksum? expected = options.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (expected is not null &&
            !string.Equals(expected.Value, actual, StringComparison.Ordinal))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.IntegrityMismatch,
                "The checksum did not match.");
        }

        BlobVersion version = NextVersion();
        BlobHead head = Head(
            key,
            version,
            bytes.LongLength,
            options.ContentType ?? new BlobMediaType("application/octet-stream"),
            options.Metadata,
            expected);
        if (!_backend.Objects.TryAdd(key, new StoredBlob(head, bytes)))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The object already exists.");
        }

        return new BlobWriteResult(head, true);
    }

    public ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_backend.Objects.TryGetValue(source, out StoredBlob? stored))
        {
            throw new BlobStoreException(BlobStoreErrorCode.NotFound, "Missing source.");
        }

        if (options.EffectiveSourceConditions.IfMatch is { } sourceVersion &&
            sourceVersion != stored.Head.Identity.Version)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The source changed.");
        }

        BlobVersion version = NextVersion();
        BlobHead head = Head(
            destination,
            version,
            stored.Head.Properties.ContentLength,
            stored.Head.Properties.ContentType,
            options.ReplacementMetadata ?? stored.Head.Properties.Metadata,
            stored.Head.Properties.Checksums.SingleOrDefault(
                checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256));
        if (!_backend.Objects.TryAdd(
                destination,
                new StoredBlob(head, stored.Bytes.ToArray())))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.PreconditionFailed,
                "The destination exists.");
        }

        return ValueTask.FromResult(new BlobCopyResult(
            head,
            stored.Head.Identity));
    }

    public ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool removed = _backend.Objects.TryRemove(key, out StoredBlob? stored);
        return ValueTask.FromResult(new BlobDeleteResult(
            removed,
            removed ? stored!.Head.Identity : null));
    }

    public IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeDirectPlanAsync is not null)
        {
            await BeforeDirectPlanAsync(cancellationToken);
        }

        LastDirectRequest = request;
        return new DirectUploadPlan(
            request.Key,
            new SignedHttpRequest(
                HttpMethodKind.Put,
                new Uri("https://storage.invalid/upload?sig=test-secret"),
                new Dictionary<string, string>
                {
                    ["Content-Type"] = request.ContentType.Value,
                    ["Content-Length"] = request.ContentLength.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                }),
            _now.AddMinutes(5),
            request.Conditions,
            request.Checksum);
    }

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string uploadId = $"multipart-{Guid.NewGuid():N}";
        return ValueTask.FromResult(new MultipartSession(
            uploadId,
            request.Key,
            _now.Add(request.SessionLifetime),
            request.ContentLength,
            request.Conditions,
            Capabilities.Limits.MaxMultipartParts,
            Capabilities.Limits.MinMultipartPartBytes,
            Capabilities.Limits.MaxMultipartPartBytes,
            request.PartPlanLifetime,
            request.ContentType,
            request.Checksum,
            request.Metadata,
            $"test:v1:{uploadId}"));
    }

    public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new MultipartPartPlan(
            session.UploadId,
            partNumber,
            new SignedHttpRequest(
                HttpMethodKind.Put,
                new Uri($"https://storage.invalid/part/{partNumber}?sig=test-secret")),
            session.MinPartBytes,
            session.MaxPartBytes,
            Min(
                _now.Add(session.PartPlanLifetime),
                session.ExpiresAtUtc)));
    }

    public ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
        => CompleteMultipartCoreAsync(session, parts, cancellationToken);

    private async ValueTask<MultipartCompletion> CompleteMultipartCoreAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeCompleteMultipartAsync is not null)
        {
            await BeforeCompleteMultipartAsync(cancellationToken);
        }

        if (!string.Equals(
                session.ProviderState,
                $"test:v1:{session.UploadId}",
                StringComparison.Ordinal))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The multipart session is invalid.");
        }

        BlobVersion version = NextVersion();
        BlobHead head = Head(
            session.Key,
            version,
            parts.Sum(part => part.SizeBytes),
            session.ContentType,
            session.Metadata,
            session.Checksum);
        _backend.Objects[session.Key] = new StoredBlob(head, []);
        if (CompleteOutcomeUnknownAfterStore)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "The multipart completion response was lost.");
        }

        return new MultipartCompletion(head);
    }

    public async ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeAbortMultipartAsync is not null)
        {
            await BeforeAbortMultipartAsync(cancellationToken);
        }

        if (!string.Equals(
                session.ProviderState,
                $"test:v1:{session.UploadId}",
                StringComparison.Ordinal))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The multipart session is invalid.");
        }

        if (AbortOutcomeUnknown)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "The multipart abort response was lost.");
        }
    }

    public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    private BlobVersion NextVersion() =>
        new($"provider-v{Interlocked.Increment(ref _backend.Version)}");

    private static DateTimeOffset Min(
        DateTimeOffset left,
        DateTimeOffset right) =>
        left < right ? left : right;

    private static BlobHead Head(
        BlobKey key,
        BlobVersion version,
        long length,
        BlobMediaType contentType,
        BlobMetadata metadata,
        BlobChecksum? checksum) =>
        new(
            new BlobIdentity(key, version),
            new BlobProperties(
                length,
                contentType,
                UploadPersistenceDatabase.Now,
                version,
                new BlobEntityTag($"etag-{version.Value}"),
                checksum is null ? [] : [checksum],
                metadata));

    private sealed record StoredBlob(BlobHead Head, byte[] Bytes);

    private sealed class Backend
    {
        internal ConcurrentDictionary<BlobKey, StoredBlob> Objects { get; } = new();

        internal long Version;
    }
}
