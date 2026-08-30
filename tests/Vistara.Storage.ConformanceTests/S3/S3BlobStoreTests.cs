using System.Text;
using System.Text.Json;
using Vistara.Application.Common.Storage;
using Vistara.Storage.ConformanceTests.Fixtures;
using Vistara.Storage.S3;

namespace Vistara.Storage.ConformanceTests.S3;

public sealed class S3BlobStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Put_streams_the_exact_key_conditions_metadata_and_checksum()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        BlobKey key = new("staging/01/tenant/upload");
        string checksumHex = new('a', 64);

        BlobWriteResult result = await store.PutAsync(
            key,
            new TrackingReplayableContent("payload"),
            new BlobWriteOptions(
                new BlobMediaType("image/jpeg"),
                new BlobMetadata([new("vistara-tenant", "tenant-01")]),
                [new BlobChecksum(BlobChecksumAlgorithm.Sha256, checksumHex)],
                BlobRequestConditions.CreateOnly),
            CancellationToken.None);

        S3PutCommand command = Assert.Single(transport.PutCommands);
        Assert.Equal(key.Value, command.Key);
        Assert.DoesNotContain("bucket", command.Key, StringComparison.Ordinal);
        Assert.True(command.Conditions.RequireMissing);
        Assert.Equal("tenant-01", command.Metadata["vistara-tenant"]);
        Assert.Equal(
            Convert.ToBase64String(Convert.FromHexString(checksumHex)),
            Assert.Single(command.Checksums).WireValue);
        Assert.Equal(key, result.Head.Identity.Key);
    }

    [Fact]
    public async Task Read_translates_an_exact_range_and_disposes_the_provider_response()
    {
        RecordingS3Transport transport = new()
        {
            ReadResult = RecordingS3Transport.CreateReadResult("2345", "range-key"),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        await using (BlobReadHandle handle = await store.OpenReadAsync(
                         new BlobKey("range-key"),
                         new BlobReadOptions(new BlobRange(2, 4)),
                         CancellationToken.None))
        {
            Assert.Equal("bytes=2-5", Assert.Single(transport.GetCommands).Range);
            Assert.Equal(new BlobContentRange(2, 4, 10), handle.ContentRange);
        }

        Assert.True(transport.ReadResult!.Disposed);
    }

    [Fact]
    public async Task Invalid_provider_read_metadata_still_disposes_the_response_stream()
    {
        S3ReadResult malformed = new(
            new MemoryStream([1, 2, 3]),
            new S3ObjectDescriptor(
                "wrong-key",
                3,
                "not-a-media-type",
                Now,
                "\"etag\"",
                [],
                new Dictionary<string, string>()),
            null);
        RecordingS3Transport transport = new()
        {
            ReadResult = malformed,
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.OpenReadAsync(
                new BlobKey("expected-key"),
                BlobReadOptions.Full,
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, error.Code);
        Assert.True(malformed.Disposed);
    }

    [Theory]
    [InlineData(S3ProviderKind.BackblazeB2)]
    public async Task Unsupported_atomic_conditions_fail_before_any_provider_request(
        S3ProviderKind provider)
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(provider, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.PutAsync(
                new BlobKey("conditional"),
                new TrackingReplayableContent("payload"),
                new BlobWriteOptions(conditions: BlobRequestConditions.CreateOnly),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
        Assert.Empty(transport.PutCommands);
        Assert.Empty(transport.HeadCommands);
    }

    [Fact]
    public async Task Cloudflare_R2_translates_documented_conditional_put_support()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.CloudflareR2, transport);

        await store.PutAsync(
            new BlobKey("r2-conditional"),
            new TrackingReplayableContent("payload"),
            new BlobWriteOptions(conditions: BlobRequestConditions.CreateOnly),
            CancellationToken.None);

        Assert.True(Assert.Single(transport.PutCommands).Conditions.RequireMissing);
    }

    [Fact]
    public async Task Direct_upload_signs_exact_method_key_headers_checksum_and_bounded_ttl()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        BlobKey key = new("staging/01/exact-key");
        BlobChecksum checksum = new(BlobChecksumAlgorithm.Sha256, new string('b', 64));

        DirectUploadPlan plan = await store.CreateDirectUploadAsync(
            new DirectUploadRequest(
                key,
                8,
                new BlobMediaType("image/jpeg"),
                checksum,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(15),
                new BlobMetadata([new("vistara-tenant", "tenant-01")])),
            CancellationToken.None);

        S3PresignCommand command = Assert.Single(transport.PresignCommands);
        Assert.Equal(HttpMethodKind.Put, command.Method);
        Assert.Equal(key.Value, command.Key);
        Assert.Equal(Now.AddMinutes(15), command.ExpiresAtUtc);
        Assert.Equal("*", command.Headers["If-None-Match"]);
        Assert.Equal("image/jpeg", command.Headers["Content-Type"]);
        Assert.Equal("8", command.Headers["Content-Length"]);
        Assert.Equal(
            Convert.ToBase64String(Convert.FromHexString(checksum.Value)),
            command.Headers["x-amz-checksum-sha256"]);
        Assert.Equal(command.Headers, plan.Request.Headers);
        Assert.Equal(Now.AddMinutes(15), plan.ExpiresAtUtc);
        Assert.DoesNotContain("signature", plan.Request.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Signed_upload_rejects_metadata_header_injection()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CreateDirectUploadAsync(
                new DirectUploadRequest(
                    new BlobKey("metadata-injection"),
                    8,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.None,
                    TimeSpan.FromMinutes(5),
                    new BlobMetadata([new("unsafe", "value\r\nAuthorization: secret")])),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
        Assert.Empty(transport.PresignCommands);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Presign_lifetime_above_configured_bound_is_rejected()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CreateReadGrantAsync(
                new BlobKey("read"),
                new ReadGrantOptions(TimeSpan.FromHours(2)),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
        Assert.Empty(transport.PresignCommands);
    }

    [Fact]
    public async Task Copy_and_delete_translate_supported_conditions_without_head_emulation()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        BlobEntityTag sourceTag = new("\"source\"");
        BlobEntityTag destinationTag = new("\"destination\"");

        await store.CopyAsync(
            new BlobKey("source"),
            new BlobKey("destination"),
            new BlobCopyOptions(
                SourceConditions: new BlobRequestConditions(
                    ifEntityTagMatch: sourceTag)),
            CancellationToken.None);
        await store.DeleteAsync(
            new BlobKey("destination"),
            new BlobDeleteOptions(new BlobRequestConditions(
                ifEntityTagMatch: destinationTag)),
            CancellationToken.None);

        Assert.Equal(sourceTag.Value, Assert.Single(transport.CopyCommands).SourceIfMatch);
        Assert.Equal(destinationTag.Value, Assert.Single(transport.DeleteCommands).IfMatch);
        Assert.Empty(transport.HeadCommands);
    }

    [Fact]
    public async Task Destination_create_only_copy_streams_a_conditioned_get_into_a_conditional_put()
    {
        const string content = "payload";
        string checksum = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(content)));
        RecordingS3Transport transport = new()
        {
            ReadResult = new S3ReadResult(
                new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
                new S3ObjectDescriptor(
                    "source",
                    content.Length,
                    "image/jpeg",
                    Now,
                    "\"source\"",
                    [new S3ChecksumValue(BlobChecksumAlgorithm.Sha256, checksum)],
                    new Dictionary<string, string>
                    {
                        ["source"] = "metadata",
                    }),
                null),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        BlobMetadata replacement = new([new("vistara-sha256", checksum)]);

        BlobCopyResult result = await store.CopyAsync(
            new BlobKey("source"),
            new BlobKey("destination"),
            new BlobCopyOptions(
                SourceConditions: new BlobRequestConditions(
                    ifEntityTagMatch: new BlobEntityTag("\"source\"")),
                DestinationConditions: BlobRequestConditions.CreateOnly,
                ReplacementMetadata: replacement),
            CancellationToken.None);

        Assert.Empty(transport.CopyCommands);
        S3GetCommand get = Assert.Single(transport.GetCommands);
        Assert.Equal("\"source\"", get.Conditions.IfMatch);
        S3PutCommand put = Assert.Single(transport.PutCommands);
        Assert.True(put.Conditions.RequireMissing);
        Assert.Equal("image/jpeg", put.ContentType);
        Assert.Equal(content.Length, put.ContentLength);
        Assert.Equal(checksum, put.Metadata["vistara-sha256"]);
        Assert.Equal(
            Convert.ToBase64String(Convert.FromHexString(checksum)),
            Assert.Single(put.Checksums).WireValue);
        Assert.Equal("destination", result.Head.Identity.Key.Value);
    }

    [Fact]
    public async Task Destination_create_only_copy_preserves_metadata_and_loses_the_create_race_safely()
    {
        StatefulS3BlobStoreFixture fixture =
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws);
        BlobKey source = fixture.Key("immutable-source");
        BlobKey destination = fixture.Key("immutable-destination");
        await fixture.SeedAsync(source, "source");
        await fixture.SeedAsync(destination, "winner");

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await fixture.Store.CopyAsync(
                source,
                destination,
                new BlobCopyOptions(
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: new BlobMetadata(
                        [new("publication", "immutable")])),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.PreconditionFailed, error.Code);
        Assert.Equal("winner", await fixture.ReadTextAsync(destination));
    }

    [Fact]
    public async Task Destination_create_only_copy_preserves_content_metadata_and_checksum()
    {
        StatefulS3BlobStoreFixture fixture =
            StatefulS3BlobStoreFixture.Create(S3ProviderKind.Aws);
        BlobKey source = fixture.Key("publication-source");
        BlobKey destination = fixture.Key("publication-destination");
        await fixture.SeedAsync(source, "source");

        await fixture.Store.CopyAsync(
            source,
            destination,
            new BlobCopyOptions(
                DestinationConditions: BlobRequestConditions.CreateOnly,
                ReplacementMetadata: new BlobMetadata(
                    [new("publication", "immutable")])),
            CancellationToken.None);

        BlobHead head = Assert.IsType<BlobHead>(
            await fixture.Store.HeadAsync(destination, CancellationToken.None));
        Assert.Equal("text/plain", head.Properties.ContentType.Value);
        Assert.Equal("immutable", head.Properties.Metadata["publication"]);
        Assert.Contains(
            head.Properties.Checksums,
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
        Assert.Equal("source", await fixture.ReadTextAsync(destination));
    }

    [Fact]
    public async Task Native_source_checksum_must_match_the_verified_promotion_checksum()
    {
        RecordingS3Transport transport = new()
        {
            ReadResult = new S3ReadResult(
                new MemoryStream("payload"u8.ToArray(), writable: false),
                new S3ObjectDescriptor(
                    "source",
                    7,
                    "image/jpeg",
                    Now,
                    "\"source\"",
                    [
                        new S3ChecksumValue(
                            BlobChecksumAlgorithm.Sha256,
                            new string('a', 64)),
                    ],
                    new Dictionary<string, string>()),
                null),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: new BlobMetadata(
                        [new("vistara-sha256", new string('b', 64))])),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, error.Code);
        Assert.Empty(transport.PutCommands);
    }

    [Fact]
    public async Task Destination_create_only_copy_is_unsupported_without_atomic_create()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.BackblazeB2, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    DestinationConditions: BlobRequestConditions.CreateOnly),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
        Assert.Empty(transport.GetCommands);
        Assert.Empty(transport.PutCommands);
        Assert.Empty(transport.HeadCommands);
    }

    [Fact]
    public async Task Destination_create_only_copy_is_unsupported_without_a_native_source_checksum()
    {
        RecordingS3Transport transport = new()
        {
            ReadResult = new S3ReadResult(
                new MemoryStream("payload"u8.ToArray(), writable: false),
                new S3ObjectDescriptor(
                    "source",
                    7,
                    "image/jpeg",
                    Now,
                    "\"source\"",
                    [],
                    new Dictionary<string, string>()),
                null),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    DestinationConditions: BlobRequestConditions.CreateOnly),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
        Assert.Empty(transport.PutCommands);
    }

    [Theory]
    [InlineData(S3ProviderKind.Aws)]
    [InlineData(S3ProviderKind.Minio)]
    public async Task Multipart_source_without_native_checksum_is_verified_before_promotion(
        S3ProviderKind provider)
    {
        const string content = "multipart payload";
        string checksum = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(content)));
        RecordingS3Transport transport = new()
        {
            ReadResultFactory = command => new S3ReadResult(
                new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
                new S3ObjectDescriptor(
                    command.Key,
                    content.Length,
                    "image/jpeg",
                    Now,
                    "\"multipart-source\"",
                    [],
                    new Dictionary<string, string>()),
                null),
        };
        S3BlobStore store = CreateStore(provider, transport);

        BlobCopyResult result = await store.CopyAsync(
            new BlobKey("source"),
            new BlobKey("destination"),
            new BlobCopyOptions(
                SourceConditions: new BlobRequestConditions(
                    ifEntityTagMatch: new BlobEntityTag("\"multipart-source\"")),
                DestinationConditions: BlobRequestConditions.CreateOnly,
                ReplacementMetadata: new BlobMetadata(
                    [new("vistara-sha256", checksum)])),
            CancellationToken.None);

        Assert.Equal(2, transport.GetCommands.Count);
        Assert.All(
            transport.GetCommands,
            command => Assert.Equal(
                "\"multipart-source\"",
                command.Conditions.IfMatch));
        S3PutCommand put = Assert.Single(transport.PutCommands);
        Assert.True(put.Conditions.RequireMissing);
        Assert.Equal(checksum, put.Metadata["vistara-sha256"]);
        Assert.Equal(
            BlobChecksumAlgorithm.Sha256,
            Assert.Single(put.Checksums).Algorithm);
        Assert.Equal("destination", result.Head.Identity.Key.Value);
    }

    [Fact]
    public async Task Independently_verified_promotion_requires_a_native_destination_checksum()
    {
        const string content = "multipart payload";
        string checksum = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(content)));
        RecordingS3Transport transport = new()
        {
            ReadResultFactory = command => new S3ReadResult(
                new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
                new S3ObjectDescriptor(
                    command.Key,
                    content.Length,
                    "image/jpeg",
                    Now,
                    "\"multipart-source\"",
                    [],
                    new Dictionary<string, string>()),
                null),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.CloudflareR2, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    SourceConditions: new BlobRequestConditions(
                        ifEntityTagMatch: new BlobEntityTag("\"multipart-source\"")),
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: new BlobMetadata(
                        [new("vistara-sha256", checksum)])),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
        Assert.Single(transport.GetCommands);
        Assert.Empty(transport.PutCommands);
    }

    [Fact]
    public async Task Independently_verified_promotion_rejects_a_checksum_mismatch_before_put()
    {
        const string content = "multipart payload";
        RecordingS3Transport transport = new()
        {
            ReadResultFactory = command => new S3ReadResult(
                new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false),
                new S3ObjectDescriptor(
                    command.Key,
                    content.Length,
                    "image/jpeg",
                    Now,
                    "\"multipart-source\"",
                    [],
                    new Dictionary<string, string>()),
                null),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    SourceConditions: new BlobRequestConditions(
                        ifEntityTagMatch: new BlobEntityTag("\"multipart-source\"")),
                    DestinationConditions: BlobRequestConditions.CreateOnly,
                    ReplacementMetadata: new BlobMetadata(
                        [new("vistara-sha256", new string('0', 64))])),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, error.Code);
        Assert.Single(transport.GetCommands);
        Assert.Empty(transport.PutCommands);
    }

    [Fact]
    public async Task Backblaze_B2_rejects_undocumented_conditional_copy_support()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.BackblazeB2, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("source"),
                new BlobKey("destination"),
                new BlobCopyOptions(
                    SourceConditions: new BlobRequestConditions(
                        ifEntityTagMatch: new BlobEntityTag("\"etag\""))),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
        Assert.Empty(transport.CopyCommands);
    }

    [Fact]
    public async Task Signed_read_preserves_exact_range_and_safe_download_disposition()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        BlobKey key = new("read/exact");

        SignedAccessPlan plan = await store.CreateReadGrantAsync(
            key,
            new ReadGrantOptions(
                TimeSpan.FromMinutes(5),
                new BlobRange(10, 5),
                "unsafe\"\r\nname.jpg"),
            CancellationToken.None);

        S3PresignCommand command = Assert.Single(transport.PresignCommands);
        Assert.Equal(HttpMethodKind.Get, command.Method);
        Assert.Equal(key.Value, command.Key);
        Assert.Equal("bytes=10-14", command.Headers["Range"]);
        Assert.Equal(
            "attachment; filename=\"unsafe___name.jpg\"",
            command.Parameters["response-content-disposition"]);
        Assert.Equal(command.Headers, plan.Request.Headers);
    }

    [Fact]
    public async Task List_preserves_provider_order_and_complete_head_properties()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        List<BlobHead> entries = [];
        await foreach (BlobHead head in store.ListAsync(
                           new BlobListOptions("prefix/"),
                           CancellationToken.None))
        {
            entries.Add(head);
        }

        BlobHead entry = Assert.Single(entries);
        Assert.Equal("prefix/listed", entry.Identity.Key.Value);
        Assert.Contains(
            entry.Properties.Checksums,
            checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256);
    }

    [Fact]
    public async Task Minio_offline_profile_supports_unconditional_direct_and_multipart_plans()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Minio, transport);
        BlobKey directKey = new("minio/direct");
        BlobKey multipartKey = new("minio/multipart");

        DirectUploadPlan direct = await store.CreateDirectUploadAsync(
            new DirectUploadRequest(
                directKey,
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.None,
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        MultipartSession multipart = await store.BeginMultipartAsync(
            new MultipartRequest(
                multipartKey,
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.None,
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        MultipartPartPlan part = await store.CreatePartPlanAsync(
            multipart,
            1,
            CancellationToken.None);

        Assert.Equal(directKey, direct.Key);
        Assert.Equal(multipartKey, multipart.Key);
        Assert.Equal(1, part.PartNumber);
    }

    [Fact]
    public async Task Minio_rejects_conditional_multipart_instead_of_emulating_it()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Minio, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.BeginMultipartAsync(
                new MultipartRequest(
                    new BlobKey("minio/conditional-multipart"),
                    5 * 1024 * 1024,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.CreateOnly,
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task Aws_rejects_sha256_as_a_full_object_multipart_checksum()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.BeginMultipartAsync(
                new MultipartRequest(
                    new BlobKey("aws/checksummed-multipart"),
                    5 * 1024 * 1024,
                    new BlobMediaType("image/jpeg"),
                    new BlobChecksum(
                        BlobChecksumAlgorithm.Sha256,
                        new string('d', 64)),
                    BlobRequestConditions.None,
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public async Task Multipart_validates_order_counts_sizes_and_completion_conditions()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("multipart"),
                10 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                BlobMetadata.Empty),
            CancellationToken.None);

        BlobStoreException orderError = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [
                    new UploadedPart(2, new BlobEntityTag("\"two\""), null, 5 * 1024 * 1024),
                    new UploadedPart(1, new BlobEntityTag("\"one\""), null, 5 * 1024 * 1024),
                ],
                CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, orderError.Code);

        await store.CompleteMultipartAsync(
            session,
            [
                new UploadedPart(1, new BlobEntityTag("\"one\""), null, 5 * 1024 * 1024),
                new UploadedPart(2, new BlobEntityTag("\"two\""), null, 5 * 1024 * 1024),
            ],
            CancellationToken.None);

        S3CompleteMultipartCommand command =
            Assert.Single(transport.CompleteMultipartCommands);
        Assert.True(command.Conditions.RequireMissing);
        Assert.Equal([1, 2], command.Parts.Select(part => part.PartNumber));
    }

    [Fact]
    public async Task Multipart_completion_transport_ambiguity_is_represented_and_reconcilable()
    {
        RecordingS3Transport transport = new()
        {
            CompleteException = new S3TransportException(
                S3TransportError.OutcomeUnknown,
                "Completion response was lost."),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("ambiguous"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                BlobMetadata.Empty),
            CancellationToken.None);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [new UploadedPart(
                    1,
                    new BlobEntityTag("\"one\""),
                    null,
                    5 * 1024 * 1024)],
                CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.OutcomeUnknown, error.Code);

        BlobHead? reconciled = await store.HeadAsync(
            session.Key,
            CancellationToken.None);
        Assert.NotNull(reconciled);
        Assert.Equal(session.Key, reconciled.Identity.Key);
    }

    [Fact]
    public async Task Durable_multipart_issuance_survives_serialization_and_a_new_store_instance()
    {
        RecordingS3Transport transport = new();
        S3ValidatedOptions options =
            new S3BlobStoreOptions(S3ProviderKind.Aws, "bucket", "us-east-1")
                .Validate();
        MultipartRequest request = new(
            new BlobKey("multipart/durable"),
            5 * 1024 * 1024,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            new BlobMetadata(
            [
                new("vistara-multipart-issuance-id", "mpi-01"),
            ]));
        IDurableMultipartBlobStore first = Assert.IsAssignableFrom<
            IDurableMultipartBlobStore>(
            new S3BlobStore(options, transport, new FixedTimeProvider(Now)));

        MultipartSession issued = await first.GetOrCreateMultipartAsync(
            "mpi-01",
            request,
            CancellationToken.None);
        string persistedState = JsonSerializer.Deserialize<string>(
            JsonSerializer.Serialize(issued.ProviderState))!;
        MultipartSession persisted = new(
            issued.UploadId,
            issued.Key,
            issued.ExpiresAtUtc,
            issued.ContentLength,
            issued.CompletionConditions,
            issued.MaxParts,
            issued.MinPartBytes,
            issued.MaxPartBytes,
            issued.PartPlanLifetime,
            issued.ContentType,
            issued.Checksum,
            issued.Metadata,
            persistedState);
        IDurableMultipartBlobStore second = Assert.IsAssignableFrom<
            IDurableMultipartBlobStore>(
            new S3BlobStore(
                options,
                transport,
                new FixedTimeProvider(Now.AddMinutes(2))));
        MultipartSession recovered = await second.GetOrCreateMultipartAsync(
            "mpi-01",
            request,
            CancellationToken.None);

        Assert.Equal(issued.UploadId, recovered.UploadId);
        Assert.Equal(issued.ProviderState, recovered.ProviderState);
        Assert.Equal(issued, persisted);
        Assert.InRange(issued.ProviderState.Length, 1, 8_192);
        Assert.DoesNotContain("signature", issued.ProviderState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", issued.ProviderState, StringComparison.OrdinalIgnoreCase);
        string decodedState = DecodeProviderState(issued.ProviderState);
        Assert.DoesNotContain("signature", decodedState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", decodedState, StringComparison.OrdinalIgnoreCase);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second.GetOrCreateMultipartAsync(
                "mpi-cancelled",
                request,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second.InspectMultipartAsync(
                persisted,
                [],
                cancellation.Token));
    }

    [Fact]
    public async Task Durable_multipart_issuance_recovers_an_unrecorded_native_upload()
    {
        const string key = "multipart/unrecorded";
        RecordingS3Transport transport = new()
        {
            ActiveMultipartUploads =
            [
                new S3MultipartUploadDescriptor(
                    key,
                    "provider-recovered-upload",
                    Now.AddMinutes(-1)),
            ],
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        MultipartSession recovered = await store.GetOrCreateMultipartAsync(
            "mpi-unrecorded",
            new MultipartRequest(
                new BlobKey(key),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);

        Assert.Equal("provider-recovered-upload", recovered.UploadId);
        Assert.Empty(transport.BeginMultipartCommands);
    }

    [Fact]
    public async Task Durable_multipart_inventory_completion_and_abort_work_from_new_instances()
    {
        RecordingS3Transport transport = new()
        {
            HeadResultFactory = _ => null,
            UploadedParts =
            [
                new S3UploadedPartDescriptor(
                    1,
                    "\"provider-etag\"",
                    5 * 1024 * 1024,
                    []),
            ],
        };
        S3ValidatedOptions options =
            new S3BlobStoreOptions(S3ProviderKind.Aws, "bucket", "us-east-1")
                .Validate();
        MultipartRequest request = new(
            new BlobKey("multipart/recovered"),
            5 * 1024 * 1024,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            BlobMetadata.Empty);
        S3BlobStore first = new(
            options,
            transport,
            new FixedTimeProvider(Now));
        MultipartSession issued =
            await first.GetOrCreateMultipartAsync(
                "mpi-recovered",
                request,
                CancellationToken.None);
        MultipartSession persisted = CloneSession(
            issued,
            providerState: JsonSerializer.Deserialize<string>(
                JsonSerializer.Serialize(issued.ProviderState))!);
        S3BlobStore second = new(
            options,
            transport,
            new FixedTimeProvider(Now.AddMinutes(2)));

        MultipartInventory inventory = await second.InspectMultipartAsync(
            persisted,
            [
                new UploadedPart(
                    1,
                    new BlobEntityTag("\"claimed-etag\""),
                    null,
                    5 * 1024 * 1024),
            ],
            CancellationToken.None);
        MultipartPartPlan refreshed = await second.CreatePartPlanAsync(
            persisted,
            1,
            CancellationToken.None);
        MultipartCompletion completed = await second.CompleteMultipartAsync(
            persisted,
            inventory.Parts,
            CancellationToken.None);

        Assert.Equal(MultipartInventoryState.Active, inventory.State);
        UploadedPart observed = Assert.Single(inventory.Parts);
        Assert.Equal("\"provider-etag\"", observed.EntityTag.Value);
        Assert.Null(observed.Checksum);
        Assert.Equal(Now.AddMinutes(7), refreshed.ExpiresAtUtc);
        Assert.Equal(persisted.Key, completed.Head.Identity.Key);

        transport.HeadResultFactory = key => new S3ObjectDescriptor(
            key,
            persisted.ContentLength,
            persisted.ContentType.Value,
            Now.AddMinutes(3),
            "\"completed\"",
            [],
            persisted.Metadata.AsReadOnly());
        S3BlobStore recovering = new(
            options,
            transport,
            new FixedTimeProvider(Now.AddMinutes(31)));
        MultipartInventory recovered = await recovering.InspectMultipartAsync(
            persisted,
            inventory.Parts,
            CancellationToken.None);
        Assert.Equal(MultipartInventoryState.Completed, recovered.State);
        Assert.NotNull(recovered.CompletedHead);

        MultipartSession aborted = await first.GetOrCreateMultipartAsync(
            "mpi-aborted-bound",
            new MultipartRequest(
                new BlobKey("multipart/aborted"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        transport.HeadResultFactory = _ => null;
        S3BlobStore abortingReplica = recovering;
        await abortingReplica.AbortMultipartAsync(
            aborted,
            CancellationToken.None);
        MultipartInventory abortedInventory =
            await abortingReplica.InspectMultipartAsync(
                aborted,
                [],
                CancellationToken.None);

        Assert.Equal(MultipartInventoryState.Aborted, abortedInventory.State);
    }

    [Fact]
    public async Task Durable_multipart_state_rejects_tampering_and_cross_scope_use()
    {
        RecordingS3Transport transport = new();
        S3ValidatedOptions options =
            new S3BlobStoreOptions(S3ProviderKind.Aws, "bucket", "us-east-1")
                .Validate();
        S3BlobStore store = new(
            options,
            transport,
            new FixedTimeProvider(Now));
        MultipartSession session = await store.GetOrCreateMultipartAsync(
            "mpi-bound",
            new MultipartRequest(
                new BlobKey("multipart/bound"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        string tamperedState = Tamper(session.ProviderState);
        MultipartSession tampered = CloneSession(
            session,
            providerState: tamperedState);
        MultipartSession crossKey = CloneSession(
            session,
            key: new BlobKey("multipart/other"));
        MultipartSession crossUpload = CloneSession(
            session,
            uploadId: "different-upload");
        S3BlobStore crossContainer = new(
            new S3BlobStoreOptions(
                S3ProviderKind.Aws,
                "other-bucket",
                "us-east-1").Validate(),
            transport,
            new FixedTimeProvider(Now));
        S3BlobStore crossProfile = new(
            new S3BlobStoreOptions(
                S3ProviderKind.Minio,
                "bucket",
                "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example"),
                ForcePathStyle = true,
                AllowedEndpointHosts = ["minio.example"],
            }.Validate(),
            transport,
            new FixedTimeProvider(Now));
        MultipartSession malformed = CloneSession(
            session,
            providerState: "s3-multipart:v2:not-valid");

        await AssertInvalidStateAsync(
            () => store.CreatePartPlanAsync(
                tampered,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => store.CreatePartPlanAsync(
                crossKey,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => store.CreatePartPlanAsync(
                crossUpload,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => crossContainer.CreatePartPlanAsync(
                session,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => crossProfile.CreatePartPlanAsync(
                session,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => store.CreatePartPlanAsync(
                malformed,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => store.GetOrCreateMultipartAsync(
                "mpi-bound",
                new MultipartRequest(
                    new BlobKey("multipart/rebound"),
                    5 * 1024 * 1024,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.CreateOnly,
                    TimeSpan.FromMinutes(30),
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None));
    }

    [Fact]
    public async Task Multipart_session_survives_new_store_instances_and_issues_fresh_part_plans()
    {
        RecordingS3Transport transport = new();
        MutableTimeProvider time = new(Now);
        S3ValidatedOptions options =
            new S3BlobStoreOptions(S3ProviderKind.Aws, "bucket", "us-east-1")
                .Validate();
        S3BlobStore first = new(options, transport, time);
        MultipartSession session = await first.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("multipart/restart"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                new BlobMetadata([new("vistara-upload-id", "upload")])),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));
        S3BlobStore replicaOne = new(options, transport, time);
        S3BlobStore replicaTwo = new(options, transport, time);
        MultipartPartPlan[] plans = await Task.WhenAll(
            replicaOne.CreatePartPlanAsync(session, 1, CancellationToken.None).AsTask(),
            replicaTwo.CreatePartPlanAsync(session, 2, CancellationToken.None).AsTask());
        time.Advance(TimeSpan.FromMinutes(29));
        await replicaOne.CompleteMultipartAsync(
            session,
            [new UploadedPart(
                1,
                new BlobEntityTag("\"one\""),
                null,
                5 * 1024 * 1024)],
            CancellationToken.None);

        Assert.All(plans, plan => Assert.Equal(Now.AddMinutes(7), plan.ExpiresAtUtc));
        Assert.Equal(Now.AddMinutes(30), session.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), session.PartPlanLifetime);
        Assert.Equal("image/jpeg", session.ContentType.Value);
        Assert.Equal("upload", session.Metadata["vistara-upload-id"]);
        Assert.Single(transport.CompleteMultipartCommands);
    }

    [Fact]
    public async Task Multipart_abort_can_run_on_a_different_replica()
    {
        RecordingS3Transport transport = new();
        S3ValidatedOptions options =
            new S3BlobStoreOptions(S3ProviderKind.Aws, "bucket", "us-east-1")
                .Validate();
        MultipartSession session = await new S3BlobStore(
            options,
            transport,
            new FixedTimeProvider(Now)).BeginMultipartAsync(
                new MultipartRequest(
                    new BlobKey("multipart/abort-replica"),
                    5 * 1024 * 1024,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.None,
                    TimeSpan.FromMinutes(30),
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None);

        await new S3BlobStore(
            options,
            transport,
            new FixedTimeProvider(Now.AddMinutes(31))).AbortMultipartAsync(
                session,
                CancellationToken.None);

        Assert.Equal(
            (session.Key.Value, session.UploadId),
            Assert.Single(transport.AbortMultipartCommands));
    }

    [Fact]
    public async Task Multipart_abort_is_idempotent_without_process_local_state()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("multipart-abort"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.None,
                TimeSpan.FromMinutes(30),
                BlobMetadata.Empty),
            CancellationToken.None);

        await store.AbortMultipartAsync(session, CancellationToken.None);
        await store.AbortMultipartAsync(session, CancellationToken.None);

        Assert.Single(transport.AbortMultipartCommands);
    }

    [Fact]
    public async Task Cloudflare_R2_requires_equal_non_final_multipart_part_sizes()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.CloudflareR2, transport);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("r2/multipart"),
                17 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.None,
                TimeSpan.FromMinutes(30),
                BlobMetadata.Empty),
            CancellationToken.None);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [
                    new UploadedPart(1, new BlobEntityTag("\"one\""), null, 5 * 1024 * 1024),
                    new UploadedPart(2, new BlobEntityTag("\"two\""), null, 6 * 1024 * 1024),
                    new UploadedPart(3, new BlobEntityTag("\"three\""), null, 6 * 1024 * 1024),
                ],
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
        Assert.Empty(transport.CompleteMultipartCommands);
    }

    [Fact]
    public async Task Cloudflare_R2_preserves_full_object_crc64_multipart_checksum()
    {
        RecordingS3Transport transport = new();
        S3BlobStore store = CreateStore(S3ProviderKind.CloudflareR2, transport);
        string checksum = Convert.ToBase64String(new byte[8]);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("r2/checksummed-multipart"),
                5 * 1024 * 1024,
                new BlobMediaType("image/jpeg"),
                new BlobChecksum(BlobChecksumAlgorithm.Crc64Nvme, checksum),
                BlobRequestConditions.None,
                TimeSpan.FromMinutes(30),
                BlobMetadata.Empty),
            CancellationToken.None);

        await store.CompleteMultipartAsync(
            session,
            [
                new UploadedPart(
                    1,
                    new BlobEntityTag("\"one\""),
                    null,
                    5 * 1024 * 1024),
            ],
            CancellationToken.None);

        Assert.Equal(
            checksum,
            Assert.Single(transport.CompleteMultipartCommands).Checksum?.WireValue);
    }

    [Fact]
    public async Task Provider_errors_and_cancellation_are_mapped_without_secret_disclosure()
    {
        RecordingS3Transport transport = new()
        {
            HeadException = new S3TransportException(
                S3TransportError.PreconditionFailed,
                "provider rejected request",
                new InvalidOperationException("sensitive-provider-body")),
        };
        S3BlobStore store = CreateStore(S3ProviderKind.Aws, transport);

        BlobStoreException mapped = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.HeadAsync(
                new BlobKey("error"),
                CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.PreconditionFailed, mapped.Code);
        Assert.DoesNotContain("sensitive-provider-body", mapped.Message, StringComparison.Ordinal);

        using CancellationTokenSource source = new();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.HeadAsync(
                new BlobKey("cancel"),
                source.Token));
    }

    private static S3BlobStore CreateStore(
        S3ProviderKind provider,
        RecordingS3Transport transport)
    {
        S3BlobStoreOptions options = provider switch
        {
            S3ProviderKind.Aws => new(provider, "bucket", "us-east-1"),
            S3ProviderKind.CloudflareR2 => new(provider, "bucket", "auto")
            {
                ServiceUrl = new Uri(
                    "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com"),
            },
            S3ProviderKind.BackblazeB2 => new(provider, "bucket", "us-east-005")
            {
                ServiceUrl = new Uri("https://s3.us-east-005.backblazeb2.com"),
                ForcePathStyle = true,
            },
            S3ProviderKind.Minio => new(provider, "bucket", "us-east-1")
            {
                ServiceUrl = new Uri("https://minio.example"),
                ForcePathStyle = true,
                AllowedEndpointHosts = ["minio.example"],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        return new S3BlobStore(
            options.Validate(),
            transport,
            new FixedTimeProvider(Now));
    }

    private static MultipartSession CloneSession(
        MultipartSession session,
        string? uploadId = null,
        BlobKey? key = null,
        string? providerState = null) =>
        new(
            uploadId ?? session.UploadId,
            key ?? session.Key,
            session.ExpiresAtUtc,
            session.ContentLength,
            session.CompletionConditions,
            session.MaxParts,
            session.MinPartBytes,
            session.MaxPartBytes,
            session.PartPlanLifetime,
            session.ContentType,
            session.Checksum,
            session.Metadata,
            providerState ?? session.ProviderState);

    private static string Tamper(string value)
    {
        int offset = value.IndexOf(':', value.IndexOf(':') + 1) + 1;
        char replacement = value[offset] == 'a' ? 'b' : 'a';
        return string.Concat(
            value[..offset],
            replacement,
            value[(offset + 1)..]);
    }

    private static string DecodeProviderState(string value)
    {
        string encoded = value[(value.LastIndexOf(':') + 1)..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(
            encoded.Length + ((4 - (encoded.Length % 4)) % 4),
            '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private static async Task AssertInvalidStateAsync<T>(
        Func<ValueTask<T>> operation)
    {
        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await operation());
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan duration) => _now += duration;
    }
}
