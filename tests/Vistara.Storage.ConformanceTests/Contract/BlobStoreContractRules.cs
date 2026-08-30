using System.Security.Cryptography;
using Vistara.Application.Common.Storage;
using Vistara.Storage.ConformanceTests.Fixtures;

namespace Vistara.Storage.ConformanceTests.Contract;

public static class BlobStoreContractRules
{
    public static async Task Conditional_operations_fail_without_mutation_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            BlobKey source = fixture.Key("source");
            BlobKey destination = fixture.Key("destination");
            await fixture.SeedAsync(source, "source-v1");
            await fixture.SeedAsync(destination, "destination-v1");
            BlobHead? sourceHead = await fixture.Store.HeadAsync(
                source,
                CancellationToken.None);
            BlobHead? destinationHead = await fixture.Store.HeadAsync(
                destination,
                CancellationToken.None);
            Assert.NotNull(sourceHead);
            Assert.NotNull(destinationHead);

            BlobStoreException readError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.OpenReadAsync(
                    source,
                    new BlobReadOptions(
                        Conditions: new BlobRequestConditions(new BlobVersion("wrong"))),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, readError.Code);

            BlobStoreException writeError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.PutAsync(
                    destination,
                    fixture.Content("replacement"),
                    new BlobWriteOptions(
                        conditions: BlobRequestConditions.CreateOnly),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, writeError.Code);

            BlobStoreException copyError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.CopyAsync(
                    source,
                    destination,
                    new BlobCopyOptions(
                        SourceConditions: new BlobRequestConditions(new BlobVersion("wrong")),
                        DestinationConditions: new BlobRequestConditions(
                            destinationHead.Identity.Version)),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, copyError.Code);

            BlobStoreException deleteError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.DeleteAsync(
                    source,
                    new BlobDeleteOptions(
                        new BlobRequestConditions(new BlobVersion("wrong"))),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.PreconditionFailed, deleteError.Code);

            Assert.Equal("source-v1", await fixture.ReadTextAsync(source));
            Assert.Equal("destination-v1", await fixture.ReadTextAsync(destination));
            Assert.Equal(sourceHead.Identity.Version, (await fixture.Store.HeadAsync(
                source,
                CancellationToken.None))?.Identity.Version);
            Assert.Equal(destinationHead.Identity.Version, (await fixture.Store.HeadAsync(
                destination,
                CancellationToken.None))?.Identity.Version);
        }
    }

    public static async Task Replayable_content_is_opened_per_operation_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            TrackingReplayableContent content = fixture.TrackingContent("replay-me");
            BlobKey first = fixture.Key("first");
            BlobKey second = fixture.Key("second");

            await fixture.Store.PutAsync(
                first,
                content,
                BlobWriteOptions.None,
                CancellationToken.None);
            await fixture.Store.PutAsync(
                second,
                content,
                BlobWriteOptions.None,
                CancellationToken.None);

            Assert.Equal(2, content.OpenCount);
            Assert.Equal("replay-me", await fixture.ReadTextAsync(first));
            Assert.Equal("replay-me", await fixture.ReadTextAsync(second));
        }
    }

    public static async Task Range_and_checksum_results_are_exact_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            BlobKey key = fixture.Key("range");
            const string content = "0123456789";
            await fixture.SeedAsync(key, content);
            await using BlobReadHandle handle = await fixture.Store.OpenReadAsync(
                key,
                new BlobReadOptions(new BlobRange(2, 4)),
                CancellationToken.None);
            using StreamReader reader = new(handle.Content);
            string observed = await reader.ReadToEndAsync(CancellationToken.None);
            string expectedChecksum = Convert.ToHexStringLower(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

            Assert.Equal("2345", observed);
            Assert.Equal(new BlobContentRange(2, 4, 10), handle.ContentRange);
            Assert.Contains(
                handle.Head.Properties.Checksums,
                checksum =>
                    checksum.Algorithm == BlobChecksumAlgorithm.Sha256 &&
                    checksum.Value == expectedChecksum);
        }
    }

    public static async Task Unsupported_conditions_never_fall_back_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            Assert.False(fixture.Store.Capabilities.SupportsConditionalCreate);
            BlobKey key = fixture.Key("unsupported-condition");

            BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.PutAsync(
                    key,
                    fixture.Content("must-not-write"),
                    new BlobWriteOptions(
                        conditions: BlobRequestConditions.CreateOnly),
                    CancellationToken.None));

            Assert.Equal(BlobStoreErrorCode.Unsupported, error.Code);
            Assert.Null(await fixture.Store.HeadAsync(key, CancellationToken.None));

            BlobStoreException directError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.CreateDirectUploadAsync(
                    fixture.DirectRequest(key),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.Unsupported, directError.Code);

            BlobStoreException multipartError = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.BeginMultipartAsync(
                    fixture.MultipartRequest(key),
                    CancellationToken.None));
            Assert.Equal(BlobStoreErrorCode.Unsupported, multipartError.Code);
        }
    }

    public static async Task Multipart_completion_requires_canonical_part_order_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            MultipartSession session = await fixture.Store.BeginMultipartAsync(
                new MultipartRequest(
                    fixture.Key("multipart"),
                    8,
                    new BlobMediaType("image/jpeg"),
                    null,
                    BlobRequestConditions.CreateOnly,
                    TimeSpan.FromMinutes(10),
                    BlobMetadata.Empty),
                CancellationToken.None);
            IReadOnlyList<UploadedPart> reversed =
            [
                new UploadedPart(2, new BlobEntityTag("etag-2"), null, 4),
                new UploadedPart(1, new BlobEntityTag("etag-1"), null, 4),
            ];

            BlobStoreException error = await Assert.ThrowsAsync<BlobStoreException>(
                async () => await fixture.Store.CompleteMultipartAsync(
                    session,
                    reversed,
                    CancellationToken.None));

            Assert.Equal(BlobStoreErrorCode.InvalidRequest, error.Code);
            Assert.Null(await fixture.Store.HeadAsync(session.Key, CancellationToken.None));
        }
    }

    public static async Task Precancelled_tokens_cancel_every_io_operation_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            BlobKey key = fixture.Key("cancel");
            MultipartSession session = fixture.Session(key);
            using CancellationTokenSource source = new();
            source.Cancel();
            CancellationToken cancellationToken = source.Token;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.HeadAsync(key, cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.OpenReadAsync(
                    key,
                    BlobReadOptions.Full,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.PutAsync(
                    key,
                    fixture.Content("cancel"),
                    BlobWriteOptions.None,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.CopyAsync(
                    key,
                    fixture.Key("copy"),
                    BlobCopyOptions.None,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.DeleteAsync(
                    key,
                    BlobDeleteOptions.None,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await EnumerateAsync(
                    fixture.Store.ListAsync(BlobListOptions.All, cancellationToken)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.CreateDirectUploadAsync(
                    fixture.DirectRequest(key),
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.BeginMultipartAsync(
                    fixture.MultipartRequest(key),
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.CreatePartPlanAsync(
                    session,
                    1,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.CompleteMultipartAsync(
                    session,
                    [new UploadedPart(1, new BlobEntityTag("etag"), null, 8)],
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.AbortMultipartAsync(
                    session,
                    cancellationToken));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await fixture.Store.CreateReadGrantAsync(
                    key,
                    new ReadGrantOptions(TimeSpan.FromMinutes(1)),
                    cancellationToken));
        }
    }

    public static async Task Listing_preserves_prefix_order_metadata_and_checksums_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            await fixture.SeedAsync(fixture.Key("list/b"), "b");
            await fixture.SeedAsync(fixture.Key("other"), "other");
            await fixture.SeedAsync(fixture.Key("list/a"), "a");
            List<BlobHead> observed = [];
            await foreach (BlobHead head in fixture.Store.ListAsync(
                               new BlobListOptions("contract/list/"),
                               CancellationToken.None))
            {
                observed.Add(head);
            }

            Assert.Equal(
                ["contract/list/a", "contract/list/b"],
                observed.Select(head => head.Identity.Key.Value));
            Assert.All(
                observed,
                head => Assert.Contains(
                    head.Properties.Checksums,
                    checksum => checksum.Algorithm == BlobChecksumAlgorithm.Sha256));
        }
    }

    public static async Task Direct_and_signed_read_plans_preserve_exact_scope_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            BlobKey uploadKey = fixture.Key("direct");
            BlobChecksum checksum = new(
                BlobChecksumAlgorithm.Sha256,
                new string('a', 64));
            DirectUploadRequest request = new(
                uploadKey,
                8,
                new BlobMediaType("image/jpeg"),
                checksum,
                BlobRequestConditions.CreateOnly,
                TimeSpan.FromMinutes(5),
                BlobMetadata.Empty);

            DirectUploadPlan plan = await fixture.Store.CreateDirectUploadAsync(
                request,
                CancellationToken.None);

            Assert.Equal(uploadKey, plan.Key);
            Assert.Equal(HttpMethodKind.Put, plan.Request.Method);
            Assert.Equal(request.Conditions, plan.Conditions);
            Assert.Equal(checksum, plan.RequiredChecksum);
            Assert.Equal(
                InMemoryBlobStoreFixture.ContractTimestamp.AddMinutes(5),
                plan.ExpiresAtUtc);

            await fixture.SeedAsync(uploadKey, "readable");
            BlobRange range = new(1, 3);
            SignedAccessPlan readPlan = await fixture.Store.CreateReadGrantAsync(
                uploadKey,
                new ReadGrantOptions(TimeSpan.FromMinutes(2), range),
                CancellationToken.None);

            Assert.Equal(uploadKey, readPlan.Key);
            Assert.Equal(HttpMethodKind.Get, readPlan.Request.Method);
            Assert.Equal(range, readPlan.Range);
            Assert.Equal(
                InMemoryBlobStoreFixture.ContractTimestamp.AddMinutes(2),
                readPlan.ExpiresAtUtc);
        }
    }

    public static async Task Multipart_plans_complete_and_abort_explicitly_async(
        IBlobStoreFixture fixture)
    {
        await using (fixture)
        {
            MultipartSession completedSession = await fixture.Store.BeginMultipartAsync(
                fixture.MultipartRequest(fixture.Key("multipart-complete")),
                CancellationToken.None);
            MultipartPartPlan partPlan = await fixture.Store.CreatePartPlanAsync(
                completedSession,
                1,
                CancellationToken.None);

            Assert.Equal(completedSession.UploadId, partPlan.UploadId);
            Assert.Equal(1, partPlan.PartNumber);
            Assert.Equal(HttpMethodKind.Put, partPlan.Request.Method);

            MultipartCompletion completion = await fixture.Store.CompleteMultipartAsync(
                completedSession,
                [
                    new UploadedPart(1, new BlobEntityTag("etag-1"), null, 4),
                    new UploadedPart(2, new BlobEntityTag("etag-2"), null, 4),
                ],
                CancellationToken.None);

            Assert.Equal(completedSession.Key, completion.Head.Identity.Key);
            Assert.Equal(8, completion.Head.Properties.ContentLength);

            MultipartSession abortedSession = await fixture.Store.BeginMultipartAsync(
                fixture.MultipartRequest(fixture.Key("multipart-abort")),
                CancellationToken.None);
            await fixture.Store.AbortMultipartAsync(
                abortedSession,
                CancellationToken.None);
        }
    }

    private static async Task EnumerateAsync(IAsyncEnumerable<BlobHead> entries)
    {
        await foreach (BlobHead _ in entries)
        {
        }
    }
}
