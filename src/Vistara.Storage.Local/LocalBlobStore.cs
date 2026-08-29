using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.Local;

/// <summary>
/// Stores blobs beneath an operator-controlled dedicated directory.
/// </summary>
/// <remarks>
/// Keys are hashed before path resolution and every existing component is
/// rejected when it is a symbolic link or reparse point. The configured root
/// and its ancestors must not be concurrently writable by untrusted principals;
/// portable path APIs cannot eliminate directory-swap TOCTOU attacks otherwise.
/// </remarks>
public sealed class LocalBlobStore : IBlobStore
{
    private static readonly byte[] FooterMagic = "VISTAR01"u8.ToArray();
    private const int FooterLength = 16;
    private const int MaximumDescriptorBytes = 1_048_576;
    private const int CopyBufferBytes = 131_072;
    private readonly LocalBlobPathGuard _pathGuard;
    private readonly string _objectsPath;
    private readonly string _locksPath;

    public LocalBlobStore(LocalBlobStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pathGuard = new LocalBlobPathGuard(options.RootPath);
        string internalPath = _pathGuard.ResolveUnderRoot(".vistara");
        _objectsPath = _pathGuard.ResolveUnderRoot(".vistara", "objects");
        _locksPath = _pathGuard.ResolveUnderRoot(".vistara", "locks");
        _pathGuard.EnsureDirectory(internalPath);
        _pathGuard.EnsureDirectory(_objectsPath);
        _pathGuard.EnsureDirectory(_locksPath);
    }

    public string Name => "local";

    public BlobStoreCapabilities Capabilities { get; } = new()
    {
        SupportsDirectUpload = false,
        SupportsMultipartUpload = false,
        SupportsRangeReads = true,
        SupportsConditionalRead = true,
        SupportsConditionalCreate = true,
        SupportsConditionalReplace = true,
        SupportsConditionalCopy = true,
        SupportsConditionalDelete = true,
        SupportsConditionalMultipartCompletion = false,
        SupportsServerSideCopy = true,
        SupportsObjectVersioning = false,
        SupportsSignedRead = false,
        ReadAfterWriteConsistency = BlobConsistencyModel.Strong,
        ListAfterWriteConsistency = BlobConsistencyModel.Strong,
        NativeChecksumAlgorithms = [BlobChecksumAlgorithm.Sha256],
        Limits = new BlobStoreLimits(long.MaxValue - MaximumDescriptorBytes, 1_024, 1, 1, 1),
    };

    public async ValueTask<BlobHead?> HeadAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string objectPath = ResolveObjectPath(key);
        if (!_pathGuard.EnsureFileIsSafeOrMissing(objectPath))
        {
            return null;
        }

        await using FileStream stream = await OpenObjectFileAsync(
            objectPath,
            cancellationToken);
        return await ReadHeadAsync(stream, key, cancellationToken);
    }

    public async ValueTask<BlobReadHandle> OpenReadAsync(
        BlobKey key,
        BlobReadOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        string objectPath = ResolveObjectPath(key);
        if (!_pathGuard.EnsureFileIsSafeOrMissing(objectPath))
        {
            throw NotFound();
        }

        FileStream? stream = null;
        try
        {
            stream = await OpenObjectFileAsync(objectPath, cancellationToken);
            BlobHead head = await ReadHeadAsync(stream, key, cancellationToken);
            CheckConditions(head, options.EffectiveConditions);

            long offset = 0;
            long length = head.Properties.ContentLength;
            BlobContentRange? contentRange = null;
            if (options.Range is not null)
            {
                BlobRange range = options.Range;
                if (range.Offset >= length ||
                    checked(range.Offset + range.Length) > length)
                {
                    throw new BlobStoreException(
                        BlobStoreErrorCode.InvalidRange,
                        "The requested byte range is outside the local blob.");
                }

                offset = range.Offset;
                length = range.Length;
                contentRange = new BlobContentRange(
                    range.Offset,
                    range.Length,
                    head.Properties.ContentLength);
            }

            stream.Position = offset;
            BoundedReadStream content = new(stream, length);
            stream = null;
            return new BlobReadHandle(content, head, contentRange);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    public async ValueTask<BlobWriteResult> PutAsync(
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ValidateChecksums(options.Checksums);
        if (content.Length < 0 || content.Length > Capabilities.Limits.MaxObjectBytes)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The declared local blob length is invalid.");
        }

        string objectPath = ResolveObjectPath(key);
        string objectDirectory = Path.GetDirectoryName(objectPath)!;
        _pathGuard.EnsureDirectory(objectDirectory);
        await using FileStream keyLock = await AcquireKeyLockAsync(
            key,
            cancellationToken);

        BlobHead? existing = await TryReadHeadAsync(
            objectPath,
            key,
            cancellationToken);
        CheckConditions(existing, options.Conditions);
        bool created = existing is null;

        string stagingPath = Path.Combine(
            objectDirectory,
            string.Concat(
                ".",
                Path.GetFileNameWithoutExtension(objectPath),
                ".",
                Guid.NewGuid().ToString("N"),
                ".staging"));
        _pathGuard.EnsureFileIsSafeOrMissing(stagingPath);

        try
        {
            BlobHead stagedHead = await WriteStagingAsync(
                stagingPath,
                key,
                content,
                options,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(stagingPath, objectPath, overwrite: !options.Conditions.RequireMissing);
            }
            catch (IOException error) when (options.Conditions.RequireMissing)
            {
                throw PreconditionFailed(error);
            }

            FlushPublishedDirectory(objectDirectory);
            return new BlobWriteResult(stagedHead, created);
        }
        finally
        {
            TryDeleteStaging(stagingPath);
        }
    }

    public async ValueTask<BlobCopyResult> CopyAsync(
        BlobKey source,
        BlobKey destination,
        BlobCopyOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);

        await using BlobReadHandle sourceHandle = await OpenReadAsync(
            source,
            new BlobReadOptions(Conditions: options.EffectiveSourceConditions),
            cancellationToken);
        BlobMetadata metadata =
            options.ReplacementMetadata ?? sourceHandle.Head.Properties.Metadata;
        BlobWriteResult result = await PutAsync(
            destination,
            new SingleOpenContent(
                sourceHandle.Content,
                sourceHandle.Head.Properties.ContentLength),
            new BlobWriteOptions(
                sourceHandle.Head.Properties.ContentType,
                metadata,
                sourceHandle.Head.Properties.Checksums,
                options.EffectiveDestinationConditions),
            cancellationToken);
        return new BlobCopyResult(result.Head, sourceHandle.Head.Identity);
    }

    public async ValueTask<BlobDeleteResult> DeleteAsync(
        BlobKey key,
        BlobDeleteOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        string objectPath = ResolveObjectPath(key);
        await using FileStream keyLock = await AcquireKeyLockAsync(
            key,
            cancellationToken);
        BlobHead? existing = await TryReadHeadAsync(
            objectPath,
            key,
            cancellationToken);
        if (existing is null)
        {
            CheckConditions(existing, options.EffectiveConditions);
            return new BlobDeleteResult(false, null);
        }

        CheckConditions(existing, options.EffectiveConditions);
        _pathGuard.EnsureFileIsSafeOrMissing(objectPath);
        File.Delete(objectPath);
        FlushPublishedDirectory(Path.GetDirectoryName(objectPath)!);
        return new BlobDeleteResult(true, existing.Identity);
    }

    public async IAsyncEnumerable<BlobHead> ListAsync(
        BlobListOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (options.IncludeVersions)
        {
            throw Unsupported("Local blob version listing is not supported.");
        }

        ValidatePrefix(options.Prefix);
        List<BlobHead> heads = [];
        foreach (string path in EnumerateObjectPaths(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream stream = await OpenObjectFileAsync(
                path,
                cancellationToken);
            LocalBlobDescriptor descriptor = await ReadDescriptorAsync(
                stream,
                cancellationToken);
            BlobKey key = CreateValidatedDescriptorKey(descriptor.Key);
            if (!string.Equals(
                    ResolveObjectPath(key),
                    path,
                    LocalBlobPathGuard.PathComparison))
            {
                throw Corrupt("A local blob descriptor does not match its resolved path.");
            }

            if (options.Prefix is null ||
                key.Value.StartsWith(options.Prefix, StringComparison.Ordinal))
            {
                heads.Add(CreateHead(key, descriptor));
            }
        }

        foreach (BlobHead head in heads.OrderBy(
                     item => item.Identity.Key.Value,
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return head;
        }
    }

    public ValueTask<DirectUploadPlan> CreateDirectUploadAsync(
        DirectUploadRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        throw Unsupported(
            "Local storage uses streaming API uploads and does not issue direct upload URLs.");
    }

    public ValueTask<MultipartSession> BeginMultipartAsync(
        MultipartRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        throw Unsupported("Local storage does not support multipart upload sessions.");
    }

    public ValueTask<MultipartPartPlan> CreatePartPlanAsync(
        MultipartSession session,
        int partNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        throw Unsupported("Local storage does not support multipart upload plans.");
    }

    public ValueTask<MultipartCompletion> CompleteMultipartAsync(
        MultipartSession session,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        throw Unsupported("Local storage does not support multipart completion.");
    }

    public ValueTask AbortMultipartAsync(
        MultipartSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(session);
        throw Unsupported("Local storage does not support multipart abort.");
    }

    public ValueTask<SignedAccessPlan> CreateReadGrantAsync(
        BlobKey key,
        ReadGrantOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(options);
        throw Unsupported(
            "Local storage streams reads through the application and does not issue signed URLs.");
    }

    private static async ValueTask<BlobHead> WriteStagingAsync(
        string stagingPath,
        BlobKey key,
        IReplayableBlobContent content,
        BlobWriteOptions options,
        CancellationToken cancellationToken)
    {
        await using FileStream destination = new(
            stagingPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan |
            FileOptions.WriteThrough);
        await using Stream source = await content.OpenReadAsync(cancellationToken);
        if (source is null || !source.CanRead)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "Replayable local blob content must provide a readable stream.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long observedLength = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(
                    buffer.AsMemory(0, CopyBufferBytes),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                observedLength = checked(observedLength + read);
                if (observedLength > content.Length)
                {
                    throw IntegrityMismatch(
                        "The local blob stream exceeded its declared length.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (observedLength != content.Length)
        {
            throw IntegrityMismatch(
                "The local blob stream did not match its declared length.");
        }

        string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        BlobChecksum? expected = options.Checksums.SingleOrDefault(
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        if (expected is not null &&
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected.Value),
                Encoding.ASCII.GetBytes(sha256)))
        {
            throw IntegrityMismatch(
                "The local blob bytes did not match the required SHA-256 checksum.");
        }

        string version = Guid.CreateVersion7().ToString("N");
        LocalBlobDescriptor descriptor = new(
            key.Value,
            observedLength,
            (options.ContentType ?? new BlobMediaType("application/octet-stream")).Value,
            DateTimeOffset.UtcNow,
            version,
            string.Concat("\"local-", version, "\""),
            sha256,
            options.Metadata.AsReadOnly().ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal));
        byte[] descriptorBytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        if (descriptorBytes.Length > MaximumDescriptorBytes)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "Local blob metadata exceeds the supported descriptor size.");
        }

        await destination.WriteAsync(descriptorBytes, cancellationToken);
        await destination.WriteAsync(FooterMagic, cancellationToken);
        byte[] descriptorLength = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(
            descriptorLength,
            descriptorBytes.LongLength);
        await destination.WriteAsync(descriptorLength, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        destination.Flush(flushToDisk: true);
        return CreateHead(key, descriptor);
    }

    private async ValueTask<FileStream> AcquireKeyLockAsync(
        BlobKey key,
        CancellationToken cancellationToken)
    {
        string hash = HashKey(key);
        string lockPath = _pathGuard.ResolveUnderRoot(
            ".vistara",
            "locks",
            string.Concat(hash, ".lock"));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pathGuard.EnsureDirectory(_locksPath);
            _pathGuard.EnsureFileIsSafeOrMissing(lockPath);
            try
            {
                FileStream stream = new(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                _pathGuard.EnsureFileIsSafeOrMissing(lockPath);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }

    private async ValueTask<BlobHead?> TryReadHeadAsync(
        string objectPath,
        BlobKey key,
        CancellationToken cancellationToken)
    {
        if (!_pathGuard.EnsureFileIsSafeOrMissing(objectPath))
        {
            return null;
        }

        await using FileStream stream = await OpenObjectFileAsync(
            objectPath,
            cancellationToken);
        return await ReadHeadAsync(stream, key, cancellationToken);
    }

    private async ValueTask<FileStream> OpenObjectFileAsync(
        string objectPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _pathGuard.EnsureFileIsSafeOrMissing(objectPath);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                objectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            _pathGuard.EnsureFileIsSafeOrMissing(objectPath);
            FileStream result = stream;
            stream = null;
            return result;
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw NotFound(error);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static async ValueTask<BlobHead> ReadHeadAsync(
        FileStream stream,
        BlobKey key,
        CancellationToken cancellationToken)
    {
        LocalBlobDescriptor descriptor = await ReadDescriptorAsync(
            stream,
            cancellationToken);
        if (!string.Equals(descriptor.Key, key.Value, StringComparison.Ordinal))
        {
            throw Corrupt("The local blob descriptor key does not match the requested key.");
        }

        return CreateHead(key, descriptor);
    }

    private static async ValueTask<LocalBlobDescriptor> ReadDescriptorAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        if (stream.Length < FooterLength)
        {
            throw Corrupt("The local blob is missing its descriptor footer.");
        }

        byte[] footer = new byte[FooterLength];
        stream.Position = stream.Length - FooterLength;
        await stream.ReadExactlyAsync(footer, cancellationToken);
        if (!footer.AsSpan(0, FooterMagic.Length).SequenceEqual(FooterMagic))
        {
            throw Corrupt("The local blob descriptor footer is invalid.");
        }

        long descriptorLength = BinaryPrimitives.ReadInt64LittleEndian(
            footer.AsSpan(FooterMagic.Length, sizeof(long)));
        if (descriptorLength <= 0 ||
            descriptorLength > MaximumDescriptorBytes ||
            descriptorLength > stream.Length - FooterLength)
        {
            throw Corrupt("The local blob descriptor length is invalid.");
        }

        long descriptorOffset = stream.Length - FooterLength - descriptorLength;
        stream.Position = descriptorOffset;
        byte[] descriptorBytes = new byte[checked((int)descriptorLength)];
        await stream.ReadExactlyAsync(descriptorBytes, cancellationToken);
        LocalBlobDescriptor descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<LocalBlobDescriptor>(
                    descriptorBytes) ??
                throw new JsonException("The descriptor was empty.");
        }
        catch (JsonException error)
        {
            throw Corrupt("The local blob descriptor is invalid.", error);
        }

        if (descriptor.ContentLength != descriptorOffset)
        {
            throw Corrupt("The local blob payload length does not match its descriptor.");
        }

        return descriptor;
    }

    private static BlobHead CreateHead(
        BlobKey key,
        LocalBlobDescriptor descriptor)
    {
        try
        {
            BlobVersion version = new(descriptor.Version);
            BlobProperties properties = new(
                descriptor.ContentLength,
                new BlobMediaType(descriptor.ContentType),
                descriptor.LastModifiedUtc,
                version,
                new BlobEntityTag(descriptor.EntityTag),
                [new BlobChecksum(BlobChecksumAlgorithm.Sha256, descriptor.Sha256)],
                new BlobMetadata(descriptor.Metadata));
            return new BlobHead(new BlobIdentity(key, version), properties);
        }
        catch (ArgumentException error)
        {
            throw Corrupt("The local blob descriptor contains invalid values.", error);
        }
    }

    private IEnumerable<string> EnumerateObjectPaths(
        CancellationToken cancellationToken)
    {
        Stack<string> pending = new();
        pending.Push(_objectsPath);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            _pathGuard.EnsureDirectoryIsSafe(directory);
            string[] entries = Directory.GetFileSystemEntries(directory);
            Array.Sort(entries, StringComparer.Ordinal);
            for (int index = entries.Length - 1; index >= 0; index--)
            {
                string entry = entries[index];
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new BlobStoreException(
                        BlobStoreErrorCode.InvalidRequest,
                        "Local blob listing will not traverse symbolic links or reparse points.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else if (entry.EndsWith(".blob", StringComparison.Ordinal))
                {
                    yield return entry;
                }
            }
        }
    }

    private string ResolveObjectPath(BlobKey key)
    {
        string hash = HashKey(key);
        return _pathGuard.ResolveUnderRoot(
            ".vistara",
            "objects",
            hash[..2],
            hash.Substring(2, 2),
            string.Concat(hash, ".blob"));
    }

    private static string HashKey(BlobKey key)
    {
        ValidateKey(key);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)));
    }

    private static void ValidateKey(BlobKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        string value = key.Value;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 1_024 ||
            value[0] == '/' ||
            value[^1] == '/' ||
            value.Contains("//", StringComparison.Ordinal) ||
            value.Split('/').Any(segment => segment is "." or "..") ||
            value.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '/' or '.' or '_' or '-')))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The local blob key is not a safe relative lowercase ASCII key.");
        }
    }

    private static BlobKey CreateValidatedDescriptorKey(string value)
    {
        try
        {
            BlobKey key = new(value);
            ValidateKey(key);
            return key;
        }
        catch (ArgumentException error)
        {
            throw Corrupt("A local blob descriptor contains an invalid key.", error);
        }
    }

    private static void ValidatePrefix(string? prefix)
    {
        if (prefix is null)
        {
            return;
        }

        if (prefix.Length > 1_024 ||
            (prefix.Length > 0 && prefix[0] == '/') ||
            prefix.Contains("//", StringComparison.Ordinal) ||
            prefix.Split('/').Any(segment => segment is "." or "..") ||
            prefix.Any(character =>
                character > 127 ||
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '/' or '.' or '_' or '-')))
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.InvalidRequest,
                "The local blob listing prefix is invalid.");
        }
    }

    private static void ValidateChecksums(IReadOnlyList<BlobChecksum> checksums)
    {
        if (checksums.Any(
                checksum => checksum.Algorithm != BlobChecksumAlgorithm.Sha256))
        {
            throw Unsupported(
                "Local storage only validates the reported native SHA-256 checksum.");
        }
    }

    private static void CheckConditions(
        BlobHead? head,
        BlobRequestConditions conditions)
    {
        bool failed =
            (conditions.RequireMissing && head is not null) ||
            (conditions.IfMatch is not null &&
             head?.Identity.Version != conditions.IfMatch) ||
            (conditions.IfEntityTagMatch is not null &&
             head?.Properties.EntityTag != conditions.IfEntityTagMatch);
        if (failed)
        {
            throw PreconditionFailed();
        }
    }

    private static void TryDeleteStaging(string stagingPath)
    {
        try
        {
            File.Delete(stagingPath);
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
        }
    }

    private static void FlushPublishedDirectory(string directoryPath)
    {
        try
        {
            LocalDirectorySync.Flush(directoryPath);
        }
        catch (IOException error)
        {
            throw new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "The local blob mutation completed but directory durability could not be confirmed.",
                error);
        }
    }

    private static BlobStoreException Unsupported(string message) =>
        new(BlobStoreErrorCode.Unsupported, message);

    private static BlobStoreException NotFound(Exception? error = null) =>
        new(BlobStoreErrorCode.NotFound, "The local blob was not found.", error);

    private static BlobStoreException PreconditionFailed(Exception? error = null) =>
        new(
            BlobStoreErrorCode.PreconditionFailed,
            "The local blob did not satisfy the requested precondition.",
            error);

    private static BlobStoreException IntegrityMismatch(string message) =>
        new(BlobStoreErrorCode.IntegrityMismatch, message);

    private static BlobStoreException Corrupt(
        string message,
        Exception? error = null) =>
        new(BlobStoreErrorCode.IntegrityMismatch, message, error);

    private sealed class SingleOpenContent(
        Stream stream,
        long length) : IReplayableBlobContent
    {
        private int _opened;

        public long Length => length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _opened, 1) != 0)
            {
                throw new InvalidOperationException(
                    "The internal local copy stream can only be opened once.");
            }

            return ValueTask.FromResult(stream);
        }
    }
}
