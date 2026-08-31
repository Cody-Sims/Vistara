using System.Globalization;
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Vistara.Application.Common.Storage;

namespace Vistara.Storage.S3;

internal sealed class AwsS3Transport : IS3Transport
{
    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly S3ProviderProfile _profile;
    private readonly bool _ownsClient;
    private readonly bool _disablePayloadSigning;

    private AwsS3Transport(
        IAmazonS3 client,
        string bucketName,
        S3ProviderProfile profile,
        bool ownsClient,
        bool disablePayloadSigning)
    {
        _client = client;
        _bucketName = bucketName;
        _profile = profile;
        _ownsClient = ownsClient;
        _disablePayloadSigning = disablePayloadSigning;
    }

    public static AwsS3Transport Create(
        S3ValidatedOptions options,
        AWSCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        AmazonS3Client client = new(credentials, CreateConfig(options));
        return new AwsS3Transport(
            client,
            options.BucketName,
            options.Profile,
            ownsClient: true,
            disablePayloadSigning:
                options.ServiceUrl?.Scheme != Uri.UriSchemeHttp);
    }

    internal static AmazonS3Config CreateConfig(S3ValidatedOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        AmazonS3Config config = new()
        {
            ForcePathStyle = options.ForcePathStyle,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (options.ServiceUrl is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl.AbsoluteUri;
            config.AuthenticationRegion = options.Region;
            config.UseHttp = options.ServiceUrl.Scheme == Uri.UriSchemeHttp;
        }

        return config;
    }

    public async ValueTask<S3ObjectDescriptor?> HeadAsync(
        string key,
        CancellationToken cancellationToken)
    {
        GetObjectMetadataRequest request = new()
        {
            BucketName = _bucketName,
            Key = key,
        };
        if (_profile.Capabilities.NativeChecksumAlgorithms.Any(
                algorithm => algorithm != BlobChecksumAlgorithm.Md5))
        {
            request.ChecksumMode = ChecksumMode.ENABLED;
        }

        try
        {
            GetObjectMetadataResponse response =
                await _client.GetObjectMetadataAsync(request, cancellationToken);
            return Descriptor(
                key,
                response.Headers.ContentLength,
                response.Headers.ContentType,
                response.LastModified ?? DateTime.UnixEpoch,
                response.ETag,
                Checksums(
                    response.ChecksumType,
                    response.ChecksumSHA256,
                    response.ChecksumCRC32,
                    response.ChecksumCRC32C,
                    response.ChecksumCRC64NVME),
                Metadata(response.Metadata));
        }
        catch (AmazonS3Exception error) when (
            error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask<S3ReadResult> GetAsync(
        S3GetCommand command,
        CancellationToken cancellationToken)
    {
        GetObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
        };
        if (command.Range is not null)
        {
            (long start, long end) = ParseRange(command.Range);
            request.ByteRange = new ByteRange(start, end);
        }

        if (command.Conditions.IfMatch is not null)
        {
            request.EtagToMatch = command.Conditions.IfMatch;
        }

        if (_profile.Capabilities.NativeChecksumAlgorithms.Any(
                algorithm => algorithm != BlobChecksumAlgorithm.Md5))
        {
            request.ChecksumMode = ChecksumMode.ENABLED;
        }

        GetObjectResponse? response = null;
        try
        {
            response = await _client.GetObjectAsync(request, cancellationToken);
            BlobContentRange? contentRange = ParseContentRange(response.ContentRange);
            long contentLength = contentRange?.TotalLength ??
                response.Headers.ContentLength;
            S3ObjectDescriptor descriptor = Descriptor(
                command.Key,
                contentLength,
                response.Headers.ContentType,
                response.LastModified ?? DateTime.UnixEpoch,
                response.ETag,
                Checksums(
                    response.ChecksumType,
                    response.ChecksumSHA256,
                    response.ChecksumCRC32,
                    response.ChecksumCRC32C,
                    response.ChecksumCRC64NVME),
                Metadata(response.Metadata));
            GetObjectResponse ownedResponse = response;
            response = null;
            return new S3ReadResult(
                ownedResponse.ResponseStream,
                descriptor,
                contentRange,
                ownedResponse.Dispose);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async ValueTask<S3ObjectDescriptor> PutAsync(
        S3PutCommand command,
        CancellationToken cancellationToken)
    {
        PutObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
            InputStream = command.Content,
            ContentType = command.ContentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false,
            UseChunkEncoding = !_disablePayloadSigning,
            DisablePayloadSigning = _disablePayloadSigning,
        };
        request.Headers.ContentLength = command.ContentLength;
        AddMetadata(request.Metadata, command.Metadata);
        AddConditions(request, command.Conditions);
        AddChecksums(request, command.Checksums);
        try
        {
            _ = await _client.PutObjectAsync(request, cancellationToken);
            return await RequireHeadAsync(command.Key, cancellationToken);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
        catch (IOException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask<S3CopyResult> CopyAsync(
        S3CopyCommand command,
        CancellationToken cancellationToken)
    {
        CopyObjectRequest request = new()
        {
            SourceBucket = _bucketName,
            SourceKey = command.SourceKey,
            DestinationBucket = _bucketName,
            DestinationKey = command.DestinationKey,
            ETagToMatch = command.SourceIfMatch,
        };
        if (command.ReplacementMetadata is not null)
        {
            request.MetadataDirective = S3MetadataDirective.REPLACE;
            AddMetadata(request.Metadata, command.ReplacementMetadata);
        }

        try
        {
            _ = await _client.CopyObjectAsync(request, cancellationToken);
            S3ObjectDescriptor destination =
                await RequireHeadAsync(command.DestinationKey, cancellationToken);
            S3ObjectDescriptor source =
                await RequireHeadAsync(command.SourceKey, cancellationToken);
            return new S3CopyResult(destination, source);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask<S3DeleteResult> DeleteAsync(
        S3DeleteCommand command,
        CancellationToken cancellationToken)
    {
        DeleteObjectRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
            IfMatch = command.IfMatch,
        };
        try
        {
            _ = await _client.DeleteObjectAsync(request, cancellationToken);
            return new S3DeleteResult(true, null);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
    }

    public async IAsyncEnumerable<S3ObjectDescriptor> ListAsync(
        string? prefix,
        bool includeVersions,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (includeVersions)
        {
            throw new S3TransportException(
                S3TransportError.Unsupported,
                "S3 object version listing is not enabled by this adapter.");
        }

        string? continuationToken = null;
        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _client.ListObjectsV2Async(
                    new ListObjectsV2Request
                    {
                        BucketName = _bucketName,
                        Prefix = prefix,
                        ContinuationToken = continuationToken,
                    },
                    cancellationToken);
            }
            catch (AmazonS3Exception error)
            {
                throw Translate(error);
            }
            catch (HttpRequestException error)
            {
                throw Unknown(error);
            }

            // AWSSDK v4 leaves response collections null rather than empty, so
            // an empty page must not be enumerated directly.
            foreach (S3Object item in response.S3Objects ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return await RequireHeadAsync(item.Key, cancellationToken);
            }

            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
        }
        while (continuationToken is not null);
    }

    public async ValueTask<string> BeginMultipartAsync(
        S3BeginMultipartCommand command,
        CancellationToken cancellationToken)
    {
        InitiateMultipartUploadRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
            ContentType = command.ContentType,
        };
        AddMetadata(request.Metadata, command.Metadata);
        if (command.ChecksumAlgorithm is not null)
        {
            request.ChecksumAlgorithm = ChecksumAlgorithm(
                command.ChecksumAlgorithm.Value);
        }

        try
        {
            InitiateMultipartUploadResponse response =
                await _client.InitiateMultipartUploadAsync(
                    request,
                    cancellationToken);
            if (string.IsNullOrWhiteSpace(response.UploadId) ||
                response.UploadId.Length > 1_024 ||
                response.UploadId.Any(char.IsControl))
            {
                throw new S3TransportException(
                    S3TransportError.IntegrityMismatch,
                    "The S3 service returned an invalid multipart upload identifier.");
            }

            return response.UploadId;
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask<IReadOnlyList<S3MultipartUploadDescriptor>>
        ListMultipartUploadsAsync(
            string key,
            CancellationToken cancellationToken)
    {
        var uploads = new List<S3MultipartUploadDescriptor>();
        string? keyMarker = null;
        string? uploadIdMarker = null;
        do
        {
            try
            {
                ListMultipartUploadsResponse response =
                    await _client.ListMultipartUploadsAsync(
                        new ListMultipartUploadsRequest
                        {
                            BucketName = _bucketName,
                            Prefix = key,
                            KeyMarker = keyMarker,
                            UploadIdMarker = uploadIdMarker,
                        },
                        cancellationToken);
                // AWSSDK v4 leaves response collections null rather than empty,
                // so a page with no uploads must not be enumerated directly.
                foreach (MultipartUpload upload in response.MultipartUploads ?? [])
                {
                    if (!string.Equals(
                            upload.Key,
                            key,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(upload.UploadId) ||
                        upload.Initiated is not { } initiated)
                    {
                        throw new S3TransportException(
                            S3TransportError.IntegrityMismatch,
                            "The S3 service returned invalid multipart upload identity.");
                    }

                    uploads.Add(new S3MultipartUploadDescriptor(
                        upload.Key,
                        upload.UploadId,
                        new DateTimeOffset(
                            DateTime.SpecifyKind(
                                initiated,
                                DateTimeKind.Utc))));
                }

                if (response.IsTruncated == true)
                {
                    string? nextKey = response.NextKeyMarker;
                    string? nextUploadId = response.NextUploadIdMarker;
                    if (string.IsNullOrWhiteSpace(nextKey) ||
                        string.IsNullOrWhiteSpace(nextUploadId) ||
                        (string.Equals(
                             nextKey,
                             keyMarker,
                             StringComparison.Ordinal) &&
                         string.Equals(
                             nextUploadId,
                             uploadIdMarker,
                             StringComparison.Ordinal)))
                    {
                        throw new S3TransportException(
                            S3TransportError.IntegrityMismatch,
                            "The S3 service returned invalid multipart upload pagination.");
                    }

                    keyMarker = nextKey;
                    uploadIdMarker = nextUploadId;
                }
                else
                {
                    keyMarker = null;
                    uploadIdMarker = null;
                }
            }
            catch (AmazonS3Exception error)
            {
                throw Translate(error);
            }
            catch (HttpRequestException error)
            {
                throw Unknown(error);
            }
        }
        while (keyMarker is not null);

        return uploads.AsReadOnly();
    }

    public async ValueTask<IReadOnlyList<S3UploadedPartDescriptor>> ListPartsAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var parts = new List<S3UploadedPartDescriptor>();
        int? partNumberMarker = null;
        do
        {
            try
            {
                ListPartsResponse response = await _client.ListPartsAsync(
                    new ListPartsRequest
                    {
                        BucketName = _bucketName,
                        Key = key,
                        UploadId = uploadId,
                        PartNumberMarker = partNumberMarker?.ToString(
                            CultureInfo.InvariantCulture),
                    },
                    cancellationToken);
                if (!string.Equals(
                        response.Key,
                        key,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        response.UploadId,
                        uploadId,
                        StringComparison.Ordinal))
                {
                    throw new S3TransportException(
                        S3TransportError.IntegrityMismatch,
                        "The S3 service returned multipart inventory for a different upload.");
                }

                // AWSSDK v4 leaves response collections null rather than empty,
                // so an upload with no parts must not be enumerated directly.
                foreach (PartDetail part in response.Parts ?? [])
                {
                    if (part.PartNumber is not { } partNumber ||
                        part.Size is not { } size ||
                        string.IsNullOrWhiteSpace(part.ETag))
                    {
                        throw new S3TransportException(
                            S3TransportError.IntegrityMismatch,
                            "The S3 service returned invalid multipart inventory.");
                    }

                    parts.Add(new S3UploadedPartDescriptor(
                        partNumber,
                        part.ETag,
                        size,
                        PartChecksums(
                            part.ChecksumSHA256,
                            part.ChecksumCRC32,
                            part.ChecksumCRC32C,
                            part.ChecksumCRC64NVME)));
                }

                if (response.IsTruncated == true)
                {
                    int? next = response.NextPartNumberMarker;
                    if (next is null ||
                        (partNumberMarker is not null &&
                         next <= partNumberMarker))
                    {
                        throw new S3TransportException(
                            S3TransportError.IntegrityMismatch,
                            "The S3 service returned invalid multipart pagination.");
                    }

                    partNumberMarker = next;
                }
                else
                {
                    partNumberMarker = null;
                }
            }
            catch (AmazonS3Exception error) when (
                error.ErrorCode == "NoSuchUpload")
            {
                throw new S3TransportException(
                    S3TransportError.NotFound,
                    "The S3 multipart upload was not found.",
                    error);
            }
            catch (AmazonS3Exception error)
            {
                throw Translate(error);
            }
            catch (HttpRequestException error)
            {
                throw Unknown(error);
            }
        }
        while (partNumberMarker is not null);

        return parts.AsReadOnly();
    }

    public async ValueTask<S3ObjectDescriptor> CompleteMultipartAsync(
        S3CompleteMultipartCommand command,
        CancellationToken cancellationToken)
    {
        CompleteMultipartUploadRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
            UploadId = command.UploadId,
        };
        foreach (S3CompletedPart part in command.Parts)
        {
            PartETag partETag = new(part.PartNumber, part.EntityTag);
            AddChecksum(partETag, part.Checksum);

            request.PartETags.Add(partETag);
        }

        AddConditions(request, command.Conditions);
        AddChecksum(request, command.Checksum);
        try
        {
            _ = await _client.CompleteMultipartUploadAsync(
                request,
                cancellationToken);
            return await RequireHeadAsync(command.Key, cancellationToken);
        }
        catch (AmazonS3Exception error)
        {
            throw new S3TransportException(
                ClassifyCompletion(error),
                "The S3 multipart completion request failed.",
                error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
        catch (IOException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask AbortMultipartAsync(
        string key,
        string uploadId,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest
                {
                    BucketName = _bucketName,
                    Key = key,
                    UploadId = uploadId,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (HttpRequestException error)
        {
            throw Unknown(error);
        }
    }

    public async ValueTask<Uri> PresignAsync(
        S3PresignCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetPreSignedUrlRequest request = new()
        {
            BucketName = _bucketName,
            Key = command.Key,
            Expires = command.ExpiresAtUtc.UtcDateTime,
            Verb = Verb(command.Method),
            UploadId = command.UploadId,
        };
        if (command.PartNumber is not null)
        {
            request.PartNumber = command.PartNumber.Value;
        }
        foreach ((string name, string value) in command.Headers)
        {
            request.Headers[name] = value;
        }

        foreach ((string name, string value) in command.Parameters)
        {
            request.Parameters[name] = value;
        }

        try
        {
            string response = await _client.GetPreSignedURLAsync(request);
            cancellationToken.ThrowIfCancellationRequested();
            return new Uri(response, UriKind.Absolute);
        }
        catch (AmazonS3Exception error)
        {
            throw Translate(error);
        }
        catch (InvalidOperationException error)
        {
            throw new S3TransportException(
                S3TransportError.InvalidRequest,
                "The AWS SDK rejected the presign request.",
                error);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<S3ObjectDescriptor> RequireHeadAsync(
        string key,
        CancellationToken cancellationToken) =>
        await HeadAsync(key, cancellationToken) ??
        throw new S3TransportException(
            S3TransportError.OutcomeUnknown,
            "The provider acknowledged a mutation but its result could not be read.");

    private static S3ObjectDescriptor Descriptor(
        string key,
        long contentLength,
        string? contentType,
        DateTime lastModified,
        string? entityTag,
        IReadOnlyList<S3ChecksumValue> checksums,
        IReadOnlyDictionary<string, string> metadata) =>
        new(
            key,
            contentLength,
            string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType,
            new DateTimeOffset(
                DateTime.SpecifyKind(lastModified, DateTimeKind.Utc)),
            string.IsNullOrWhiteSpace(entityTag) ? "\"unknown\"" : entityTag,
            checksums,
            metadata);

    private static Dictionary<string, string> Metadata(
        MetadataCollection metadata)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string rawKey in metadata.Keys)
        {
            string key = rawKey.StartsWith(
                "x-amz-meta-",
                StringComparison.OrdinalIgnoreCase)
                ? rawKey["x-amz-meta-".Length..]
                : rawKey;
            result[key.ToLowerInvariant()] = metadata[rawKey];
        }

        return result;
    }

    private static List<S3ChecksumValue> Checksums(
        ChecksumType? checksumType,
        string? sha256,
        string? crc32,
        string? crc32C,
        string? crc64Nvme)
    {
        List<S3ChecksumValue> values = [];
        if (!string.IsNullOrWhiteSpace(sha256) &&
            checksumType != ChecksumType.COMPOSITE)
        {
            values.Add(
                new S3ChecksumValue(
                    BlobChecksumAlgorithm.Sha256,
                    DecodeSha256(sha256)));
        }

        AddChecksum(values, BlobChecksumAlgorithm.Crc32, crc32);
        AddChecksum(values, BlobChecksumAlgorithm.Crc32C, crc32C);
        AddChecksum(values, BlobChecksumAlgorithm.Crc64Nvme, crc64Nvme);
        return values;
    }

    private static List<S3ChecksumValue> PartChecksums(
        string? sha256,
        string? crc32,
        string? crc32C,
        string? crc64Nvme)
    {
        var values = new List<S3ChecksumValue>();
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            values.Add(
                new S3ChecksumValue(
                    BlobChecksumAlgorithm.Sha256,
                    DecodeSha256(sha256)));
        }

        AddChecksum(values, BlobChecksumAlgorithm.Crc32, crc32);
        AddChecksum(values, BlobChecksumAlgorithm.Crc32C, crc32C);
        AddChecksum(values, BlobChecksumAlgorithm.Crc64Nvme, crc64Nvme);
        return values;
    }

    private static void AddChecksum(
        List<S3ChecksumValue> values,
        BlobChecksumAlgorithm algorithm,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(new S3ChecksumValue(algorithm, value));
        }
    }

    private static string DecodeSha256(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            if (bytes.Length != 32)
            {
                throw new FormatException();
            }

            return Convert.ToHexStringLower(bytes);
        }
        catch (FormatException error)
        {
            throw new S3TransportException(
                S3TransportError.IntegrityMismatch,
                "The S3 service returned an invalid SHA-256 checksum.",
                error);
        }
    }

    private static void AddMetadata(
        MetadataCollection destination,
        IReadOnlyDictionary<string, string> metadata)
    {
        foreach ((string key, string value) in metadata)
        {
            destination[key] = value;
        }
    }

    private static void AddConditions(
        PutObjectRequest request,
        S3Conditions conditions)
    {
        request.IfMatch = conditions.IfMatch;
        request.IfNoneMatch = conditions.RequireMissing ? "*" : null;
    }

    private static void AddConditions(
        CompleteMultipartUploadRequest request,
        S3Conditions conditions)
    {
        request.IfMatch = conditions.IfMatch;
        request.IfNoneMatch = conditions.RequireMissing ? "*" : null;
    }

    private static void AddChecksums(
        PutObjectRequest request,
        IReadOnlyList<S3WireChecksum> checksums)
    {
        foreach (S3WireChecksum checksum in checksums)
        {
            switch (checksum.Algorithm)
            {
                case BlobChecksumAlgorithm.Sha256:
                    request.ChecksumSHA256 = checksum.WireValue;
                    break;
                case BlobChecksumAlgorithm.Md5:
                    request.MD5Digest = checksum.WireValue;
                    break;
                case BlobChecksumAlgorithm.Crc32:
                    request.ChecksumCRC32 = checksum.WireValue;
                    break;
                case BlobChecksumAlgorithm.Crc32C:
                    request.ChecksumCRC32C = checksum.WireValue;
                    break;
                case BlobChecksumAlgorithm.Crc64Nvme:
                    request.ChecksumCRC64NVME = checksum.WireValue;
                    break;
                default:
                    throw new S3TransportException(
                        S3TransportError.Unsupported,
                        "The checksum is not supported by the AWS S3 transport.");
            }
        }
    }

    private static void AddChecksum(
        CompleteMultipartUploadRequest request,
        S3WireChecksum? checksum)
    {
        if (checksum is null)
        {
            return;
        }

        switch (checksum.Algorithm)
        {
            case BlobChecksumAlgorithm.Sha256:
                request.ChecksumSHA256 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Md5:
                request.ChecksumMD5 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc32:
                request.ChecksumCRC32 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc32C:
                request.ChecksumCRC32C = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc64Nvme:
                request.ChecksumCRC64NVME = checksum.WireValue;
                break;
            default:
                throw new S3TransportException(
                    S3TransportError.Unsupported,
                    "The checksum is not supported by multipart completion.");
        }
    }

    private static void AddChecksum(PartETag part, S3WireChecksum? checksum)
    {
        if (checksum is null)
        {
            return;
        }

        switch (checksum.Algorithm)
        {
            case BlobChecksumAlgorithm.Sha256:
                part.ChecksumSHA256 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Md5:
                part.ChecksumMD5 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc32:
                part.ChecksumCRC32 = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc32C:
                part.ChecksumCRC32C = checksum.WireValue;
                break;
            case BlobChecksumAlgorithm.Crc64Nvme:
                part.ChecksumCRC64NVME = checksum.WireValue;
                break;
            default:
                throw new S3TransportException(
                    S3TransportError.Unsupported,
                    "The part checksum is not supported by multipart completion.");
        }
    }

    private static ChecksumAlgorithm ChecksumAlgorithm(
        BlobChecksumAlgorithm algorithm) =>
        algorithm switch
        {
            BlobChecksumAlgorithm.Sha256 => Amazon.S3.ChecksumAlgorithm.SHA256,
            BlobChecksumAlgorithm.Crc32 => Amazon.S3.ChecksumAlgorithm.CRC32,
            BlobChecksumAlgorithm.Crc32C => Amazon.S3.ChecksumAlgorithm.CRC32C,
            BlobChecksumAlgorithm.Crc64Nvme =>
                Amazon.S3.ChecksumAlgorithm.CRC64NVME,
            _ => throw new S3TransportException(
                S3TransportError.Unsupported,
                "The multipart checksum algorithm is not supported."),
        };

    private static HttpVerb Verb(HttpMethodKind method) =>
        method switch
        {
            HttpMethodKind.Get => HttpVerb.GET,
            HttpMethodKind.Put => HttpVerb.PUT,
            HttpMethodKind.Post => throw new S3TransportException(
                S3TransportError.Unsupported,
                "The AWS SDK presigner does not support POST requests."),
            HttpMethodKind.Delete => HttpVerb.DELETE,
            _ => throw new ArgumentOutOfRangeException(nameof(method)),
        };

    private static (long Start, long End) ParseRange(string range)
    {
        string[] values = range["bytes=".Length..].Split('-', 2);
        return (
            long.Parse(values[0], CultureInfo.InvariantCulture),
            long.Parse(values[1], CultureInfo.InvariantCulture));
    }

    private static BlobContentRange? ParseContentRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] sections = value["bytes ".Length..].Split(['-', '/'], 3);
        long start = long.Parse(sections[0], CultureInfo.InvariantCulture);
        long end = long.Parse(sections[1], CultureInfo.InvariantCulture);
        long total = long.Parse(sections[2], CultureInfo.InvariantCulture);
        return new BlobContentRange(start, checked(end - start + 1), total);
    }

    internal static S3TransportError Classify(AmazonS3Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.StatusCode switch
        {
            HttpStatusCode.NotFound when error.ErrorCode == "NoSuchUpload" =>
                S3TransportError.InvalidRequest,
            HttpStatusCode.NotFound => S3TransportError.NotFound,
            HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict =>
                S3TransportError.PreconditionFailed,
            HttpStatusCode.RequestedRangeNotSatisfiable =>
                S3TransportError.InvalidRange,
            HttpStatusCode.NotImplemented => S3TransportError.Unsupported,
            HttpStatusCode.BadRequest when error.ErrorCode is
                "BadDigest" or "InvalidDigest" =>
                S3TransportError.IntegrityMismatch,
            HttpStatusCode.BadRequest => S3TransportError.InvalidRequest,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                S3TransportError.InvalidRequest,
            _ => S3TransportError.OutcomeUnknown,
        };
    }

    internal static S3TransportError ClassifyCompletion(
        AmazonS3Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.ErrorCode == "NoSuchUpload"
            ? S3TransportError.OutcomeUnknown
            : Classify(error);
    }

    private static S3TransportException Translate(AmazonS3Exception error) =>
        new(
            Classify(error),
            "The S3 service returned an error.",
            error);

    private static S3TransportException Unknown(Exception error) =>
        new(
            S3TransportError.OutcomeUnknown,
            "The S3 request outcome could not be confirmed.",
            error);
}
