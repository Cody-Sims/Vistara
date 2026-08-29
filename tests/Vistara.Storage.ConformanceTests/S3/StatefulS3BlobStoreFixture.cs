using System.Security.Cryptography;
using System.Text;
using Vistara.Application.Common.Storage;
using Vistara.Storage.ConformanceTests.Fixtures;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

internal sealed class StatefulS3BlobStoreFixture : IBlobStoreFixture
{
    private readonly StatefulS3Transport _transport = new();

    private StatefulS3BlobStoreFixture(S3ProviderKind provider)
    {
        S3BlobStoreOptions options = provider switch
        {
            S3ProviderKind.Aws => new(provider, "contract-bucket", "us-east-1"),
            S3ProviderKind.Minio => new(provider, "contract-bucket", "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example"),
                ForcePathStyle = true,
                AllowedEndpointHosts = ["minio.example"],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        Store = new S3BlobStore(
            options.Validate(),
            _transport,
            new FixedTimeProvider(InMemoryBlobStoreFixture.ContractTimestamp));
    }

    public IBlobStore Store { get; }

    public static StatefulS3BlobStoreFixture Create(S3ProviderKind provider) =>
        new(provider);

    public BlobKey Key(string suffix) => new($"contract/{suffix}");

    public IReplayableBlobContent Content(string value) =>
        new TrackingReplayableContent(value);

    public TrackingReplayableContent TrackingContent(string value) => new(value);

    public async ValueTask SeedAsync(BlobKey key, string value)
    {
        await Store.PutAsync(
            key,
            Content(value),
            new BlobWriteOptions(contentType: new BlobMediaType("text/plain")),
            CancellationToken.None);
    }

    public async ValueTask<string> ReadTextAsync(BlobKey key)
    {
        await using BlobReadHandle handle = await Store.OpenReadAsync(
            key,
            BlobReadOptions.Full,
            CancellationToken.None);
        using StreamReader reader = new(handle.Content, Encoding.UTF8);
        return await reader.ReadToEndAsync(CancellationToken.None);
    }

    public DirectUploadRequest DirectRequest(BlobKey key) =>
        new(
            key,
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartRequest MultipartRequest(BlobKey key) =>
        new(
            key,
            5 * 1024 * 1024,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.None,
            TimeSpan.FromMinutes(10),
            BlobMetadata.Empty);

    public MultipartSession Session(BlobKey key) =>
        new(
            "session-cancel",
            key,
            InMemoryBlobStoreFixture.ContractTimestamp.AddMinutes(10),
            5 * 1024 * 1024,
            BlobRequestConditions.None,
            10_000,
            5 * 1024 * 1024,
            5L * 1024 * 1024 * 1024);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed class StatefulS3Transport : IS3Transport
{
    private readonly object _gate = new();
    private readonly Dictionary<string, StoredObject> _objects =
        new(StringComparer.Ordinal);
    private long _version;

    public ValueTask<S3ObjectDescriptor?> HeadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(
                _objects.TryGetValue(key, out StoredObject? stored)
                    ? stored.Descriptor
                    : null);
        }
    }

    public ValueTask<S3ReadResult> GetAsync(
        S3GetCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_objects.TryGetValue(command.Key, out StoredObject? stored))
            {
                throw NotFound();
            }

            CheckConditions(stored.Descriptor, command.Conditions);
            byte[] bytes = stored.Bytes;
            BlobContentRange? contentRange = null;
            if (command.Range is not null)
            {
                string[] values = command.Range["bytes=".Length..].Split('-', 2);
                int start = int.Parse(
                    values[0],
                    System.Globalization.CultureInfo.InvariantCulture);
                int end = int.Parse(
                    values[1],
                    System.Globalization.CultureInfo.InvariantCulture);
                if (start < 0 || end < start || end >= bytes.Length)
                {
                    throw new S3TransportException(
                        S3TransportError.InvalidRange,
                        "Invalid range.");
                }

                contentRange = new BlobContentRange(
                    start,
                    end - start + 1,
                    bytes.Length);
                bytes = bytes[start..(end + 1)];
            }

            return ValueTask.FromResult(
                new S3ReadResult(
                    new MemoryStream(bytes, writable: false),
                    stored.Descriptor,
                    contentRange));
        }
    }

    public async ValueTask<S3ObjectDescriptor> PutAsync(
        S3PutCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream destination = new();
        await command.Content.CopyToAsync(destination, cancellationToken);
        byte[] bytes = destination.ToArray();
        if (bytes.LongLength != command.ContentLength)
        {
            throw new S3TransportException(
                S3TransportError.IntegrityMismatch,
                "Length mismatch.");
        }

        lock (_gate)
        {
            _objects.TryGetValue(command.Key, out StoredObject? existing);
            CheckConditions(existing?.Descriptor, command.Conditions);
            S3ObjectDescriptor descriptor = CreateDescriptor(
                command.Key,
                bytes,
                command.ContentType,
                command.Metadata);
            _objects[command.Key] = new StoredObject(bytes, descriptor);
            return descriptor;
        }
    }

    public ValueTask<S3CopyResult> CopyAsync(
        S3CopyCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_objects.TryGetValue(command.SourceKey, out StoredObject? source))
            {
                throw NotFound();
            }

            if (command.SourceIfMatch is not null &&
                source.Descriptor.EntityTag != command.SourceIfMatch)
            {
                throw Precondition();
            }

            S3ObjectDescriptor destination = CreateDescriptor(
                command.DestinationKey,
                source.Bytes,
                source.Descriptor.ContentType,
                command.ReplacementMetadata ?? source.Descriptor.Metadata);
            _objects[command.DestinationKey] =
                new StoredObject(source.Bytes.ToArray(), destination);
            return ValueTask.FromResult(
                new S3CopyResult(destination, source.Descriptor));
        }
    }

    public ValueTask<S3DeleteResult> DeleteAsync(
        S3DeleteCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_objects.TryGetValue(command.Key, out StoredObject? stored))
            {
                return ValueTask.FromResult(new S3DeleteResult(false, null));
            }

            if (command.IfMatch is not null &&
                stored.Descriptor.EntityTag != command.IfMatch)
            {
                throw Precondition();
            }

            _objects.Remove(command.Key);
            return ValueTask.FromResult(
                new S3DeleteResult(true, stored.Descriptor));
        }
    }

    public async IAsyncEnumerable<S3ObjectDescriptor> ListAsync(
        string? prefix,
        bool includeVersions,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (includeVersions)
        {
            throw new S3TransportException(
                S3TransportError.Unsupported,
                "Versions are unsupported.");
        }

        S3ObjectDescriptor[] snapshot;
        lock (_gate)
        {
            snapshot = _objects.Values
                .Select(value => value.Descriptor)
                .Where(value =>
                    prefix is null ||
                    value.Key.StartsWith(prefix, StringComparison.Ordinal))
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToArray();
        }

        foreach (S3ObjectDescriptor descriptor in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return descriptor;
        }
    }

    public ValueTask<string> BeginMultipartAsync(
        S3BeginMultipartCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Guid.NewGuid().ToString("N"));
    }

    public ValueTask<S3ObjectDescriptor> CompleteMultipartAsync(
        S3CompleteMultipartCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = new byte[checked((int)command.Parts.Sum(part => part.SizeBytes))];
        lock (_gate)
        {
            CheckConditions(
                _objects.TryGetValue(command.Key, out StoredObject? existing)
                    ? existing.Descriptor
                    : null,
                command.Conditions);
            S3ObjectDescriptor descriptor = CreateDescriptor(
                command.Key,
                bytes,
                "application/octet-stream",
                new Dictionary<string, string>());
            _objects[command.Key] = new StoredObject(bytes, descriptor);
            return ValueTask.FromResult(descriptor);
        }
    }

    public ValueTask AbortMultipartAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<Uri> PresignAsync(
        S3PresignCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new Uri($"https://signed.invalid/{command.Key}?signature=redacted"));
    }

    private S3ObjectDescriptor CreateDescriptor(
        string key,
        byte[] bytes,
        string contentType,
        IReadOnlyDictionary<string, string> metadata)
    {
        string version = Interlocked.Increment(ref _version)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new S3ObjectDescriptor(
            key,
            bytes.LongLength,
            contentType,
            InMemoryBlobStoreFixture.ContractTimestamp,
            $"\"etag-{version}\"",
            [
                new S3ChecksumValue(
                    BlobChecksumAlgorithm.Sha256,
                    Convert.ToHexStringLower(SHA256.HashData(bytes))),
            ],
            metadata);
    }

    private static void CheckConditions(
        S3ObjectDescriptor? descriptor,
        S3Conditions conditions)
    {
        if ((conditions.RequireMissing && descriptor is not null) ||
            (conditions.IfMatch is not null &&
             descriptor?.EntityTag != conditions.IfMatch))
        {
            throw Precondition();
        }
    }

    private static S3TransportException NotFound() =>
        new(S3TransportError.NotFound, "Not found.");

    private static S3TransportException Precondition() =>
        new(S3TransportError.PreconditionFailed, "Precondition failed.");

    private sealed record StoredObject(
        byte[] Bytes,
        S3ObjectDescriptor Descriptor);
}
