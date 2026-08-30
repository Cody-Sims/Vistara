using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vistara.Application.Common.Storage;
using Vistara.Storage.Azure;
using Vistara.Storage.ConformanceTests.Fixtures;

namespace Vistara.Storage.ConformanceTests.Azure;

public sealed class AzureBlobStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 28, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Azure_head_download_and_delete_preserve_exact_key_conditions_and_range()
    {
        RecordingAzureClient client = new();
        AzureBlobObject blob = Blob("contract/exact", "payload");
        client.HeadResult = blob;
        TrackingStream stream = new(Encoding.UTF8.GetBytes("ayl"));
        client.DownloadResult = new AzureBlobDownload(
            stream,
            blob,
            new AzureBlobRange(1, 3),
            7);
        client.DeleteResult = new AzureBlobDeleteResult(true, blob);
        AzureBlobStore store = CreateStore(client);
        BlobRequestConditions conditions =
            new(ifEntityTagMatch: new BlobEntityTag("\"etag-1\""));

        BlobHead? head = await store.HeadAsync(
            new BlobKey("contract/exact"),
            CancellationToken.None);
        await using (BlobReadHandle handle = await store.OpenReadAsync(
                         new BlobKey("contract/exact"),
                         new BlobReadOptions(new BlobRange(1, 3), conditions),
                         CancellationToken.None))
        {
            Assert.Equal(new BlobContentRange(1, 3, 7), handle.ContentRange);
            Assert.Equal("ayl", await ReadAsync(handle.Content));
            Assert.False(stream.Disposed);
        }

        BlobDeleteResult deleted = await store.DeleteAsync(
            new BlobKey("contract/exact"),
            new BlobDeleteOptions(conditions),
            CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("contract/exact", client.HeadKey);
        Assert.Equal("contract/exact", client.DownloadKey);
        Assert.Equal(new AzureBlobRange(1, 3), client.DownloadRange);
        Assert.Equal("\"etag-1\"", client.DownloadConditions?.IfMatch);
        Assert.Equal("\"etag-1\"", client.DeleteConditions?.IfMatch);
        Assert.True(deleted.Deleted);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Azure_put_streams_blocks_once_and_commits_hash_metadata_and_create_condition()
    {
        RecordingAzureClient client = new();
        client.CommitResult = Blob("contract/put", "abcdefgh");
        AzureBlobStore store = CreateStore(
            client,
            options => options.TransferBlockBytes = 4);
        TrackingReplayableContent content = new("abcdefgh");
        BlobMetadata metadata = new(
            [new KeyValuePair<string, string>("asset.kind", "original")]);

        BlobWriteResult result = await store.PutAsync(
            new BlobKey("contract/put"),
            content,
            new BlobWriteOptions(
                new BlobMediaType("text/plain"),
                metadata,
                conditions: BlobRequestConditions.CreateOnly),
            CancellationToken.None);

        Assert.Equal(1, content.OpenCount);
        Assert.Equal(["abcd", "efgh"], client.StagedContents);
        Assert.Equal(client.StagedBlockIds, client.CommittedBlockIds);
        Assert.All(client.StagedMd5, hash => Assert.Equal(16, hash.Length));
        Assert.Equal("text/plain", client.CommitOptions?.ContentType);
        Assert.True(client.CommitOptions?.Conditions.RequireMissing);
        Assert.Equal(
            "original",
            client.CommitOptions?.Metadata["vistara_m_61737365742e6b696e64"]);
        Assert.Equal(
            Sha256("abcdefgh").Value,
            client.CommitOptions?.Metadata["vistara_sha256"]);
        Assert.True(result.Created);
    }

    [Fact]
    public async Task Azure_put_rejects_length_or_checksum_mismatch_before_commit()
    {
        RecordingAzureClient client = new();
        AzureBlobStore store = CreateStore(client);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.PutAsync(
                new BlobKey("contract/mismatch"),
                new DeclaredLengthContent("actual", 7),
                new BlobWriteOptions(checksums: [Sha256("different")]),
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.IntegrityMismatch, error.Code);
        Assert.Empty(client.CommittedBlockIds);
    }

    [Fact]
    public async Task Azure_listing_maps_metadata_checksums_and_keeps_service_order()
    {
        RecordingAzureClient client = new();
        client.ListResults =
        [
            Blob("contract/list/a", "a"),
            Blob("contract/list/b", "b"),
        ];
        AzureBlobStore store = CreateStore(client);

        List<BlobHead> observed = [];
        await foreach (BlobHead head in store.ListAsync(
                           new BlobListOptions("contract/list/"),
                           CancellationToken.None))
        {
            observed.Add(head);
        }

        Assert.Equal("contract/list/", client.ListPrefix);
        Assert.Equal(
            ["contract/list/a", "contract/list/b"],
            observed.Select(head => head.Identity.Key.Value));
        Assert.All(
            observed,
            head => Assert.Contains(
                head.Properties.Checksums,
                checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256));
        Assert.All(
            observed,
            head => Assert.False(head.Properties.Metadata.TryGetValue(
                "vistara-sha256",
                out _)));
    }

    [Fact]
    public async Task Azure_direct_and_read_grants_are_bounded_exact_and_least_privilege()
    {
        RecordingAzureClient client = new();
        client.SasUri = new Uri(
            "https://account123.blob.core.windows.net/media/contract/exact?sig=secret");
        AzureBlobStore store = CreateStore(client);
        BlobKey key = new("contract/exact");
        BlobChecksum checksum = Sha256("abcdefgh");

        DirectUploadPlan upload = await store.CreateDirectUploadAsync(
            new DirectUploadRequest(
                key,
                8,
                new BlobMediaType("image/jpeg"),
                checksum,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(5),
                new BlobMetadata(
                    [new KeyValuePair<string, string>("tenant", "tenant-1")])),
            CancellationToken.None);
        BlobRange range = new(2, 4);
        SignedAccessPlan read = await store.CreateReadGrantAsync(
            key,
            new ReadGrantOptions(
                TimeSpan.FromMinutes(2),
                range,
                "photo.jpg"),
            CancellationToken.None);

        Assert.Equal(AzureBlobSasAccess.Create, client.SasRequests[0].Access);
        Assert.Equal(AzureBlobSasAccess.Read, client.SasRequests[1].Access);
        Assert.All(client.SasRequests, request => Assert.True(request.HttpsOnly));
        Assert.All(client.SasRequests, request => Assert.Equal(key.Value, request.Key));
        Assert.Equal(Now.AddMinutes(5), upload.ExpiresAtUtc);
        Assert.Equal(Now.AddMinutes(2), read.ExpiresAtUtc);
        Assert.Equal("bytes=2-5", read.Request.Headers["Range"]);
        Assert.Equal("BlockBlob", upload.Request.Headers["x-ms-blob-type"]);
        Assert.Equal("*", upload.Request.Headers["If-None-Match"]);
        Assert.Equal(checksum.Value, upload.Request.Headers["x-ms-meta-vistara_sha256"]);
        Assert.Equal(
            "tenant-1",
            upload.Request.Headers["x-ms-meta-vistara_m_74656e616e74"]);
        Assert.Equal("[signed request redacted]", upload.Request.ToString().Split(' ', 2)[1]);
    }

    [Fact]
    public async Task Azure_rejects_overlong_grants_and_header_injection()
    {
        RecordingAzureClient client = new();
        AzureBlobStore store = CreateStore(client);
        BlobKey key = new("contract/grant");

        BlobStoreException lifetimeError =
            await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.CreateReadGrantAsync(
                    key,
                    new ReadGrantOptions(TimeSpan.FromHours(2)),
                    CancellationToken.None));
        BlobStoreException fileNameError =
            await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.CreateReadGrantAsync(
                    key,
                    new ReadGrantOptions(
                        TimeSpan.FromMinutes(1),
                        downloadFileName: "photo.jpg\r\nx-evil: yes"),
                    CancellationToken.None));
        BlobStoreException metadataError =
            await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.CreateDirectUploadAsync(
                    new DirectUploadRequest(
                        key,
                        8,
                        new BlobMediaType("image/jpeg"),
                        null,
                        BlobRequestConditions.CreateOnly,
                        TimeSpan.FromMinutes(1),
                        new BlobMetadata(
                            [new("unsafe", "value\r\nAuthorization: secret")])),
                    CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.InvalidRequest, lifetimeError.Code);
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, fileNameError.Code);
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, metadataError.Code);
        Assert.Empty(client.SasRequests);
        Assert.DoesNotContain(
            "secret",
            metadataError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Azure_emulator_grants_are_the_only_http_protocol_exception()
    {
        RecordingAzureClient client = new()
        {
            SasUri = new Uri(
                "http://127.0.0.1:10000/devstoreaccount1/media/contract/emulator?sig=fake"),
            BlobUriFactory = key =>
                new Uri(
                    $"http://127.0.0.1:10000/devstoreaccount1/media/{key}"),
        };
        AzureBlobStoreOptions options =
            new(
                "devstoreaccount1",
                "media",
                new Uri("http://127.0.0.1:10000/devstoreaccount1"),
                emulatorMode: true)
            {
                CredentialMode = AzureBlobCredentialMode.ConnectionString,
                ConnectionString =
                    "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                    "AccountKey=offline;BlobEndpoint=" +
                    "http://127.0.0.1:10000/devstoreaccount1;",
                SasMode = AzureBlobSasMode.SharedKey,
                AllowSharedKeySas = true,
                TimeProvider = new FixedTimeProvider(Now),
            };
        AzureBlobStore store = new(options, new FixedFactory(client));

        _ = await store.CreateReadGrantAsync(
            new BlobKey("contract/emulator"),
            new ReadGrantOptions(TimeSpan.FromMinutes(1)),
            CancellationToken.None);

        Assert.False(Assert.Single(client.SasRequests).HttpsOnly);
    }

    [Fact]
    public async Task Azure_multipart_uses_canonical_block_ids_and_stateless_abort()
    {
        RecordingAzureClient client = new();
        client.CommitResult = Blob(
            "contract/multipart",
            "abcdefgh") with
        {
            ContentType = "image/jpeg",
        };
        AzureBlobStore store = CreateStore(client);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("contract/multipart"),
                8,
                new BlobMediaType("image/jpeg"),
                Sha256("abcdefgh"),
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(10),
                BlobMetadata.Empty),
            CancellationToken.None);

        MultipartPartPlan first = await store.CreatePartPlanAsync(
            session,
            1,
            CancellationToken.None);
        MultipartPartPlan second = await store.CreatePartPlanAsync(
            session,
            2,
            CancellationToken.None);
        await store.CompleteMultipartAsync(
            session,
            [
                new UploadedPart(1, new BlobEntityTag("\"one\""), null, 4),
                new UploadedPart(2, new BlobEntityTag("\"two\""), null, 4),
            ],
            CancellationToken.None);

        Assert.NotEqual(
            client.SasRequests[0].BlockId,
            client.SasRequests[1].BlockId);
        Assert.Equal(AzureBlobSasAccess.WriteBlock, client.SasRequests[0].Access);
        Assert.Equal(
            [client.SasRequests[0].BlockId!, client.SasRequests[1].BlockId!],
            client.CommittedBlockIds);
        Assert.Equal(session.UploadId, first.UploadId);
        Assert.Equal(session.UploadId, second.UploadId);

        MultipartSession aborted = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("contract/aborted"),
                4,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(10),
                BlobMetadata.Empty),
            CancellationToken.None);
        await store.AbortMultipartAsync(aborted, CancellationToken.None);
        BlobStoreException abortedPlan =
            await Assert.ThrowsAsync<BlobStoreException>(
                async () => await store.CreatePartPlanAsync(
                    aborted,
                    1,
                    CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, abortedPlan.Code);
    }

    [Fact]
    public async Task Azure_multipart_rejects_reordered_or_incomplete_parts_without_commit()
    {
        RecordingAzureClient client = new();
        AzureBlobStore store = CreateStore(client);
        MultipartSession session = await store.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("contract/multipart-order"),
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(10),
                BlobMetadata.Empty),
            CancellationToken.None);

        BlobStoreException reordered = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [
                    new UploadedPart(2, new BlobEntityTag("\"two\""), null, 4),
                    new UploadedPart(1, new BlobEntityTag("\"one\""), null, 4),
                ],
                CancellationToken.None));
        BlobStoreException incomplete = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [new UploadedPart(1, new BlobEntityTag("\"one\""), null, 4)],
                CancellationToken.None));

        Assert.Equal(BlobStoreErrorCode.InvalidRequest, reordered.Code);
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, incomplete.Code);
        Assert.Empty(client.CommittedBlockIds);
    }

    [Fact]
    public async Task Azure_durable_multipart_issuance_survives_serialization_and_a_new_instance()
    {
        RecordingAzureClient client = new();
        MultipartRequest request = new(
            new BlobKey("contract/durable-multipart"),
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            new BlobMetadata(
            [
                new("vistara-multipart-issuance-id", "mpi-azure-01"),
            ]));
        IDurableMultipartBlobStore first = Assert.IsAssignableFrom<
            IDurableMultipartBlobStore>(CreateStore(client));

        MultipartSession issued = await first.GetOrCreateMultipartAsync(
            "mpi-azure-01",
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
            CreateStore(
                client,
                options => options.TimeProvider =
                    new FixedTimeProvider(Now.AddMinutes(2))));
        MultipartSession recovered = await second.GetOrCreateMultipartAsync(
            "mpi-azure-01",
            request,
            CancellationToken.None);

        Assert.Equal(issued.UploadId, recovered.UploadId);
        Assert.Equal(issued.ProviderState, recovered.ProviderState);
        Assert.Equal(issued, persisted);
        Assert.InRange(issued.ProviderState.Length, 1, 8_192);
        Assert.DoesNotContain("sig=", issued.ProviderState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountkey", issued.ProviderState, StringComparison.OrdinalIgnoreCase);
        string decodedState = DecodeProviderState(issued.ProviderState);
        Assert.DoesNotContain("sig=", decodedState, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountkey", decodedState, StringComparison.OrdinalIgnoreCase);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second.GetOrCreateMultipartAsync(
                "mpi-azure-cancelled",
                request,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await second.InspectMultipartAsync(
                persisted,
                [],
                cancellation.Token));
    }

    [Fact]
    public async Task Azure_durable_state_does_not_capture_connection_credentials()
    {
        const string secret = "super-secret-account-key";
        RecordingAzureClient client = new();
        AzureBlobStoreOptions options = new(
            "account123",
            "media",
            new Uri("https://account123.blob.core.windows.net"))
        {
            CredentialMode = AzureBlobCredentialMode.ConnectionString,
            ConnectionString =
                $"DefaultEndpointsProtocol=https;AccountName=account123;AccountKey={secret};",
            SasMode = AzureBlobSasMode.SharedKey,
            AllowSharedKeySas = true,
            TimeProvider = new FixedTimeProvider(Now),
        };
        AzureBlobStore store = new(options, new FixedFactory(client));

        MultipartSession session = await store.GetOrCreateMultipartAsync(
            "mpi-credential-check",
            new MultipartRequest(
                new BlobKey("contract/credential-check"),
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);

        string decodedState = DecodeProviderState(session.ProviderState);
        Assert.DoesNotContain(secret, session.ProviderState, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, decodedState, StringComparison.Ordinal);
        Assert.DoesNotContain("accountKey", decodedState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Azure_durable_inventory_completion_abort_and_recovery_use_new_instances()
    {
        RecordingAzureClient client = new()
        {
            CommitResult = Blob(
                "contract/durable-recovery",
                "abcdefgh") with
            {
                ContentType = "image/jpeg",
            },
        };
        MultipartRequest request = new(
            new BlobKey("contract/durable-recovery"),
            8,
            new BlobMediaType("image/jpeg"),
            null,
            BlobRequestConditions.CreateOnly,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            BlobMetadata.Empty);
        AzureBlobStore first = CreateStore(client);
        MultipartSession issued = await first.GetOrCreateMultipartAsync(
            "mpi-azure-recovery",
            request,
            CancellationToken.None);
        MultipartPartPlan firstPart = await first.CreatePartPlanAsync(
            issued,
            1,
            CancellationToken.None);
        MultipartPartPlan secondPart = await first.CreatePartPlanAsync(
            issued,
            2,
            CancellationToken.None);
        string firstBlockId = client.SasRequests[^2].BlockId!;
        string secondBlockId = client.SasRequests[^1].BlockId!;
        client.BlockListResult = new AzureBlobBlockList(
            [],
            [
                new AzureBlobBlock(firstBlockId, 4),
                new AzureBlobBlock(secondBlockId, 4),
            ]);
        MultipartSession persisted = CloneSession(
            issued,
            providerState: JsonSerializer.Deserialize<string>(
                JsonSerializer.Serialize(issued.ProviderState))!);
        AzureBlobStore second = CreateStore(
            client,
            options => options.TimeProvider =
                new FixedTimeProvider(Now.AddMinutes(2)));
        UploadedPart[] claims =
        [
            new UploadedPart(
                1,
                new BlobEntityTag("\"first\""),
                null,
                5),
            new UploadedPart(
                2,
                new BlobEntityTag("\"second\""),
                null,
                4),
            new UploadedPart(
                3,
                new BlobEntityTag("\"not-uploaded\""),
                null,
                1),
        ];

        MultipartInventory active = await second.InspectMultipartAsync(
            persisted,
            claims,
            CancellationToken.None);
        MultipartCompletion completed = await second.CompleteMultipartAsync(
            persisted,
            active.Parts,
            CancellationToken.None);

        Assert.Equal(MultipartInventoryState.Active, active.State);
        Assert.Equal([1, 2], active.Parts.Select(part => part.PartNumber));
        Assert.Equal([4L, 4L], active.Parts.Select(part => part.SizeBytes));
        Assert.Equal(issued.UploadId, firstPart.UploadId);
        Assert.Equal(issued.UploadId, secondPart.UploadId);
        Assert.Equal(persisted.Key, completed.Head.Identity.Key);

        client.HeadResult = Blob(
            "contract/durable-recovery",
            "abcdefgh") with
        {
            ContentType = "image/jpeg",
        };
        client.BlockListResult = new AzureBlobBlockList(
            [
                new AzureBlobBlock(firstBlockId, 4),
                new AzureBlobBlock(secondBlockId, 4),
            ],
            []);
        AzureBlobStore recovering = CreateStore(
            client,
            options => options.TimeProvider =
                new FixedTimeProvider(Now.AddMinutes(31)));
        MultipartInventory recovered = await recovering.InspectMultipartAsync(
            persisted,
            active.Parts,
            CancellationToken.None);
        Assert.Equal(MultipartInventoryState.Completed, recovered.State);
        Assert.Equal([1, 2], recovered.Parts.Select(part => part.PartNumber));

        MultipartSession aborted = await first.GetOrCreateMultipartAsync(
            "mpi-azure-aborted",
            new MultipartRequest(
                new BlobKey("contract/durable-aborted"),
                4,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        await recovering.AbortMultipartAsync(
            aborted,
            CancellationToken.None);
        client.HeadResult = null;
        MultipartInventory abortedInventory =
            await recovering.InspectMultipartAsync(
                aborted,
                [],
                CancellationToken.None);
        Assert.Equal(MultipartInventoryState.Aborted, abortedInventory.State);
    }

    [Fact]
    public async Task Azure_durable_state_rejects_tampering_and_cross_scope_use()
    {
        RecordingAzureClient client = new();
        AzureBlobStore store = CreateStore(client);
        MultipartSession session = await store.GetOrCreateMultipartAsync(
            "mpi-azure-bound",
            new MultipartRequest(
                new BlobKey("contract/durable-bound"),
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        MultipartSession tampered = CloneSession(
            session,
            providerState: Tamper(session.ProviderState));
        MultipartSession crossKey = CloneSession(
            session,
            key: new BlobKey("contract/durable-other"));
        MultipartSession crossUpload = CloneSession(
            session,
            uploadId: "different-upload");
        AzureBlobStore crossContainer = CreateStore(
            client,
            options => options.ContainerName = "other-media");
        MultipartSession malformed = CloneSession(
            session,
            providerState: "azure-multipart:v2:not-valid");

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
            () => store.CreatePartPlanAsync(
                malformed,
                1,
                CancellationToken.None));
        await AssertInvalidStateAsync(
            () => store.GetOrCreateMultipartAsync(
                "mpi-azure-bound",
                new MultipartRequest(
                    new BlobKey("contract/durable-rebound"),
                    8,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.CreateOnly,
                    TimeSpan.FromMinutes(30),
                    TimeSpan.FromMinutes(5),
                    BlobMetadata.Empty),
                CancellationToken.None));
    }

    [Fact]
    public async Task Azure_durable_completion_ambiguity_remains_reconcilable()
    {
        RecordingAzureClient client = new();
        AzureBlobStore store = CreateStore(client);
        MultipartSession session = await store.GetOrCreateMultipartAsync(
            "mpi-azure-ambiguous",
            new MultipartRequest(
                new BlobKey("contract/durable-ambiguous"),
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);
        client.TargetCommitError = new AzureBlobClientException(
            AzureBlobClientErrorCode.OutcomeUnknown,
            "provider response contained sensitive details");

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CompleteMultipartAsync(
                session,
                [
                    new UploadedPart(
                        1,
                        new BlobEntityTag("\"one\""),
                        null,
                        4),
                    new UploadedPart(
                        2,
                        new BlobEntityTag("\"two\""),
                        null,
                        4),
                ],
                CancellationToken.None));
        MultipartInventory inventory = await store.InspectMultipartAsync(
            session,
            [],
            CancellationToken.None);

        Assert.Equal(BlobStoreErrorCode.OutcomeUnknown, error.Code);
        Assert.DoesNotContain(
            "sensitive",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MultipartInventoryState.Active, inventory.State);
    }

    [Fact]
    public async Task Azure_multipart_session_survives_new_instances_and_replica_part_refresh()
    {
        RecordingAzureClient client = new()
        {
            CommitResult = Blob(
                "contract/multipart-restart",
                "abcdefgh") with
            {
                ContentType = "image/jpeg",
            },
        };
        MutableTimeProvider time = new(Now);
        AzureBlobStore first = CreateStore(
            client,
            options => options.TimeProvider = time);
        MultipartSession session = await first.BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("contract/multipart-restart"),
                8,
                new BlobMediaType("image/jpeg"),
                Sha256("abcdefgh"),
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                new BlobMetadata([new("vistara-upload-id", "upload")])),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2));
        AzureBlobStore replicaOne = CreateStore(
            client,
            options => options.TimeProvider = time);
        AzureBlobStore replicaTwo = CreateStore(
            client,
            options => options.TimeProvider = time);
        MultipartPartPlan[] plans = await Task.WhenAll(
            replicaOne.CreatePartPlanAsync(session, 1, CancellationToken.None).AsTask(),
            replicaTwo.CreatePartPlanAsync(session, 2, CancellationToken.None).AsTask());
        time.Advance(TimeSpan.FromMinutes(29));
        await replicaOne.CompleteMultipartAsync(
            session,
            [
                new UploadedPart(1, new BlobEntityTag("\"one\""), null, 4),
                new UploadedPart(2, new BlobEntityTag("\"two\""), null, 4),
            ],
            CancellationToken.None);

        Assert.All(plans, plan => Assert.Equal(Now.AddMinutes(7), plan.ExpiresAtUtc));
        Assert.Equal(Now.AddMinutes(30), session.ExpiresAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), session.PartPlanLifetime);
        Assert.Equal("image/jpeg", client.CommitOptions?.ContentType);
        Assert.Equal(
            "upload",
            client.CommitOptions?.Metadata["vistara_m_766973746172612d75706c6f61642d6964"]);
    }

    [Fact]
    public async Task Azure_multipart_abort_is_stateless_across_replicas()
    {
        RecordingAzureClient client = new();
        MutableTimeProvider time = new(Now);
        MultipartSession session = await CreateStore(
            client,
            options => options.TimeProvider = time).BeginMultipartAsync(
            new MultipartRequest(
                new BlobKey("contract/multipart-abort-restart"),
                8,
                new BlobMediaType("image/jpeg"),
                null,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(30),
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty),
            CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(31));
        await CreateStore(
            client,
            options => options.TimeProvider = time).AbortMultipartAsync(
            session,
            CancellationToken.None);
    }

    [Fact]
    public async Task Azure_copy_polls_pending_to_success_and_maps_known_failure()
    {
        RecordingAzureClient client = new();
        AzureBlobObject source = Blob("contract/source", "source");
        AzureBlobObject destination = Blob("contract/destination", "source");
        client.HeadResult = source;
        client.CopyStates.Enqueue(new AzureBlobCopyState(AzureBlobCopyStatus.Pending, null));
        client.CopyStates.Enqueue(new AzureBlobCopyState(AzureBlobCopyStatus.Success, destination));
        AzureBlobStore store = CreateStore(
            client,
            options =>
            {
                options.CopyPollInterval = TimeSpan.Zero;
                options.MaximumCopyPollAttempts = 3;
            });

        BlobCopyResult copied = await store.CopyAsync(
            new BlobKey("contract/source"),
            new BlobKey("contract/destination"),
            new BlobCopyOptions(
                new BlobRequestConditions(ifEntityTagMatch: new BlobEntityTag("\"etag-1\"")),
                BlobRequestConditions.CreateOnly),
            CancellationToken.None);

        Assert.Equal(Identity(source), copied.Source);
        Assert.Equal("\"etag-1\"", client.CopyOptions?.SourceConditions.IfMatch);
        Assert.True(client.CopyOptions?.DestinationConditions.RequireMissing);

        client.CopyStates.Enqueue(
            new AzureBlobCopyState(
                AzureBlobCopyStatus.Failed,
                null,
                "provider detail"));
        BlobStoreException failed = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("contract/source"),
                new BlobKey("contract/failed"),
                BlobCopyOptions.None,
                CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.InvalidRequest, failed.Code);
        Assert.DoesNotContain("provider detail", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_copy_pending_exhaustion_is_outcome_unknown_and_cancellation_propagates()
    {
        RecordingAzureClient client = new();
        client.HeadResult = Blob("contract/source", "source");
        client.DefaultCopyState =
            new AzureBlobCopyState(AzureBlobCopyStatus.Pending, null);
        AzureBlobStore store = CreateStore(
            client,
            options =>
            {
                options.CopyPollInterval = TimeSpan.Zero;
                options.MaximumCopyPollAttempts = 2;
            });

        BlobStoreException unknown = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.CopyAsync(
                new BlobKey("contract/source"),
                new BlobKey("contract/destination"),
                BlobCopyOptions.None,
                CancellationToken.None));
        Assert.Equal(BlobStoreErrorCode.OutcomeUnknown, unknown.Code);

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.CopyAsync(
                new BlobKey("contract/source"),
                new BlobKey("contract/cancelled"),
                BlobCopyOptions.None,
                cancellation.Token));
    }

    [Theory]
    [InlineData(AzureBlobClientErrorCode.PreconditionFailed, BlobStoreErrorCode.PreconditionFailed)]
    [InlineData(AzureBlobClientErrorCode.InvalidRange, BlobStoreErrorCode.InvalidRange)]
    [InlineData(AzureBlobClientErrorCode.OutcomeUnknown, BlobStoreErrorCode.OutcomeUnknown)]
    public async Task Azure_maps_provider_errors_without_exposing_provider_messages(
        AzureBlobClientErrorCode providerCode,
        BlobStoreErrorCode expected)
    {
        RecordingAzureClient client = new()
        {
            HeadError = new AzureBlobClientException(
                providerCode,
                "AccountKey=secret&sig=signed"),
        };
        AzureBlobStore store = CreateStore(client);

        BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
            async () => await store.HeadAsync(
                new BlobKey("contract/error"),
                CancellationToken.None));

        Assert.Equal(expected, error.Code);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("signed", error.Message, StringComparison.Ordinal);
    }

    private static AzureBlobStore CreateStore(
        RecordingAzureClient client,
        Action<MutableOptions>? configure = null)
    {
        MutableOptions mutable = new();
        configure?.Invoke(mutable);
        AzureBlobStoreOptions options =
            new(
                "account123",
                mutable.ContainerName,
                new Uri("https://account123.blob.core.windows.net"))
            {
                TokenCredential = new TestTokenCredential(),
                TransferBlockBytes = mutable.TransferBlockBytes,
                CopyPollInterval = mutable.CopyPollInterval,
                MaximumCopyPollAttempts = mutable.MaximumCopyPollAttempts,
                TimeProvider = mutable.TimeProvider,
            };
        return new AzureBlobStore(options, new FixedFactory(client));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "The test models Azure Blob MD5 transport integrity metadata.")]
    private static AzureBlobObject Blob(string key, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new AzureBlobObject(
            key,
            bytes.LongLength,
            "text/plain",
            Now,
            "\"etag-1\"",
            "\"etag-1\"",
            MD5.HashData(bytes),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["vistara_m_74656e616e74"] = "tenant-1",
                ["vistara_sha256"] = Sha256(content).Value,
            });
    }

    private static BlobIdentity Identity(AzureBlobObject blob) =>
        new(new BlobKey(blob.Key), new BlobVersion(blob.Version));

    private static BlobChecksum Sha256(string value) =>
        new(
            BlobChecksumAlgorithm.Sha256,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))));

    private static async Task<string> ReadAsync(Stream stream)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(CancellationToken.None);
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

    private sealed class MutableOptions
    {
        public string ContainerName { get; set; } = "media";

        public int TransferBlockBytes { get; set; } = 4 * 1024 * 1024;

        public TimeSpan CopyPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        public int MaximumCopyPollAttempts { get; set; } = 480;

        public TimeProvider TimeProvider { get; set; } =
            new FixedTimeProvider(Now);
    }

    private sealed class FixedFactory(IAzureBlobClient client) : IAzureBlobClientFactory
    {
        public IAzureBlobClient CreateWithTokenCredential(
            Uri serviceUri,
            string accountName,
            string containerName,
            global::Azure.Core.TokenCredential credential,
            bool emulatorMode) =>
            client;

        public IAzureBlobClient CreateWithConnectionString(
            string connectionString,
            Uri serviceUri,
            string accountName,
            string containerName,
            bool emulatorMode) =>
            client;
    }

    private sealed class RecordingAzureClient : AzureBlobClientBase
    {
        private readonly Dictionary<(string Key, string BlockId), byte[]>
            _stagedBlocks = [];
        private readonly Dictionary<string, StoredControlBlob> _controlBlobs =
            new(StringComparer.Ordinal);
        private long _controlVersion;
        public string? HeadKey { get; private set; }
        public string? DownloadKey { get; private set; }
        public AzureBlobRange? DownloadRange { get; private set; }
        public AzureBlobConditions? DownloadConditions { get; private set; }
        public AzureBlobConditions? DeleteConditions { get; private set; }
        public AzureBlobConditions? HeadConditions { get; private set; }
        public string? ListPrefix { get; private set; }
        public AzureBlobCopyOptions? CopyOptions { get; private set; }
        public AzureBlobCommitOptions? CommitOptions { get; private set; }
        public AzureBlobObject? HeadResult { get; set; }
        public AzureBlobDownload? DownloadResult { get; set; }
        public AzureBlobObject? CommitResult { get; set; }
        public AzureBlobDeleteResult DeleteResult { get; set; } =
            new(false, null);
        public AzureBlobClientException? HeadError { get; set; }
        public AzureBlobClientException? TargetCommitError { get; set; }
        public Uri? SasUri { get; set; }
        public Func<string, Uri>? BlobUriFactory { get; set; }
        public List<AzureBlobObject> ListResults { get; set; } = [];
        public List<string> StagedBlockIds { get; } = [];
        public List<string> StagedContents { get; } = [];
        public List<byte[]> StagedMd5 { get; } = [];
        public IReadOnlyList<string> CommittedBlockIds { get; private set; } = [];
        public List<AzureBlobSasRequest> SasRequests { get; } = [];
        public Queue<AzureBlobCopyState> CopyStates { get; } = new();
        public AzureBlobCopyState DefaultCopyState { get; set; } =
            new(AzureBlobCopyStatus.Success, null);
        public AzureBlobBlockList BlockListResult { get; set; } =
            new([], []);

        public override ValueTask<AzureBlobObject?> HeadAsync(
            string key,
            AzureBlobConditions conditions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HeadError is not null)
            {
                throw HeadError;
            }

            HeadKey = key;
            HeadConditions = conditions;
            if (IsControlKey(key))
            {
                _controlBlobs.TryGetValue(
                    key,
                    out StoredControlBlob? control);
                CheckControlConditions(control, conditions);
                return ValueTask.FromResult<AzureBlobObject?>(
                    control?.Descriptor);
            }

            return ValueTask.FromResult(HeadResult);
        }

        public override ValueTask<AzureBlobDownload> DownloadAsync(
            string key,
            AzureBlobRange? range,
            AzureBlobConditions conditions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadKey = key;
            DownloadRange = range;
            DownloadConditions = conditions;
            if (IsControlKey(key))
            {
                if (!_controlBlobs.TryGetValue(
                        key,
                        out StoredControlBlob? control))
                {
                    throw new AzureBlobClientException(
                        AzureBlobClientErrorCode.NotFound,
                        "Control blob not found.");
                }

                CheckControlConditions(control, conditions);
                return ValueTask.FromResult(new AzureBlobDownload(
                    new MemoryStream(control.Content, writable: false),
                    control.Descriptor,
                    null,
                    control.Content.LongLength));
            }

            return ValueTask.FromResult(DownloadResult!);
        }

        public override async ValueTask StageBlockAsync(
            string key,
            string blockId,
            Stream content,
            byte[] contentMd5,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StagedBlockIds.Add(blockId);
            string value = await ReadAsync(content);
            StagedContents.Add(value);
            StagedMd5.Add(contentMd5);
            _stagedBlocks[(key, blockId)] = Encoding.UTF8.GetBytes(value);
        }

        public override ValueTask<AzureBlobBlockList> GetBlockListAsync(
            string key,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(BlockListResult);
        }

        public override ValueTask<AzureBlobObject> CommitBlockListAsync(
            string key,
            IReadOnlyList<string> blockIds,
            AzureBlobCommitOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsControlKey(key))
            {
                _controlBlobs.TryGetValue(
                    key,
                    out StoredControlBlob? existing);
                CheckControlConditions(existing, options.Conditions);
                using MemoryStream content = new();
                foreach (string blockId in blockIds)
                {
                    if (!_stagedBlocks.TryGetValue(
                            (key, blockId),
                            out byte[]? block))
                    {
                        throw new AzureBlobClientException(
                            AzureBlobClientErrorCode.InvalidRequest,
                            "Control block not found.");
                    }

                    content.Write(block);
                }

                byte[] bytes = content.ToArray();
                string version =
                    $"\"control-{Interlocked.Increment(ref _controlVersion)}\"";
                AzureBlobObject descriptor = new(
                    key,
                    bytes.LongLength,
                    options.ContentType,
                    Now,
                    version,
                    version,
                    null,
                    options.Metadata);
                _controlBlobs[key] = new StoredControlBlob(bytes, descriptor);
                return ValueTask.FromResult(descriptor);
            }

            if (TargetCommitError is not null)
            {
                throw TargetCommitError;
            }

            CommittedBlockIds = blockIds.ToArray();
            CommitOptions = options;
            AzureBlobObject result = CommitResult!;
            return ValueTask.FromResult(result with
            {
                ContentType = options.ContentType,
                ContentMd5 = options.ContentMd5.Length == 0
                    ? result.ContentMd5
                    : options.ContentMd5,
                Metadata = options.Metadata,
            });
        }

        public override ValueTask<AzureBlobCopyState> StartCopyAsync(
            string sourceKey,
            string destinationKey,
            AzureBlobCopyOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyOptions = options;
            return ValueTask.FromResult(NextCopyState());
        }

        public override ValueTask<AzureBlobCopyState> GetCopyStateAsync(
            string destinationKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NextCopyState());
        }

        public override ValueTask<AzureBlobDeleteResult> DeleteAsync(
            string key,
            AzureBlobConditions conditions,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteConditions = conditions;
            return ValueTask.FromResult(DeleteResult);
        }

        public override async IAsyncEnumerable<AzureBlobObject> ListAsync(
            string? prefix,
            bool includeVersions,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            ListPrefix = prefix;
            foreach (AzureBlobObject blob in ListResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return blob;
            }
        }

        public override ValueTask<Uri> CreateSasUriAsync(
            AzureBlobSasRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SasRequests.Add(request);
            return ValueTask.FromResult(
                SasUri ??
                new Uri(
                    $"https://account123.blob.core.windows.net/media/{request.Key}?sig=secret"));
        }

        public override Uri GetBlobUri(string key) =>
            BlobUriFactory?.Invoke(key) ??
            new Uri($"https://account123.blob.core.windows.net/media/{key}");

        private AzureBlobCopyState NextCopyState() =>
            CopyStates.TryDequeue(out AzureBlobCopyState? state)
                ? state
                : DefaultCopyState;

        private static bool IsControlKey(string key) =>
            key.StartsWith(
                "vistara-internal/multipart/v1/",
                StringComparison.Ordinal);

        private static void CheckControlConditions(
            StoredControlBlob? blob,
            AzureBlobConditions conditions)
        {
            if ((conditions.RequireMissing && blob is not null) ||
                (conditions.IfMatch is not null &&
                 !string.Equals(
                     blob?.Descriptor.EntityTag,
                     conditions.IfMatch,
                     StringComparison.Ordinal)))
            {
                throw new AzureBlobClientException(
                    AzureBlobClientErrorCode.PreconditionFailed,
                    "Control blob precondition failed.");
            }
        }

        private sealed record StoredControlBlob(
            byte[] Content,
            AzureBlobObject Descriptor);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;

        internal void Advance(TimeSpan duration) => _value += duration;
    }

    private sealed class TestTokenCredential : global::Azure.Core.TokenCredential
    {
        public override global::Azure.Core.AccessToken GetToken(
            global::Azure.Core.TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test", DateTimeOffset.MaxValue);

        public override ValueTask<global::Azure.Core.AccessToken> GetTokenAsync(
            global::Azure.Core.TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                new global::Azure.Core.AccessToken("test", DateTimeOffset.MaxValue));
    }

    private sealed class DeclaredLengthContent(
        string value,
        long length) : IReplayableBlobContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(value);

        public long Length => length;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(
                new MemoryStream(_bytes, writable: false));
        }
    }

    private sealed class TrackingStream(byte[] bytes)
        : MemoryStream(bytes, writable: false)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
