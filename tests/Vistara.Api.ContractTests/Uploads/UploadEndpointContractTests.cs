using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Common;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Idempotency;
using Vistara.Contracts.Uploads;
using Vistara.Storage.S3;
using Xunit;

namespace Vistara.Api.ContractTests.Uploads;

public sealed class UploadEndpointContractTests
{
    private const string Checksum =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000401");
    private static readonly Guid ActorId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000402");
    private static readonly Guid UploadId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000403");
    private static readonly Guid OtherUploadId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000404");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 2, 0, 0, TimeSpan.Zero);

    internal static Guid TenantIdForFakes => TenantId;

    internal static Guid ActorIdForFakes => ActorId;

    internal static DateTimeOffset NowForFakes => Now;

    internal static UploadProviderPolicy PolicyForFakes() => Policy();

    internal static UploadSessionSnapshot SnapshotForFakes(
        string strategy = "direct",
        string state = "uploadIssued",
        long version = 2,
        long expectedSizeBytes = 1_000) =>
        Snapshot(strategy, state, version, expectedSizeBytes);

    [Fact]
    public async Task All_upload_routes_are_protected_by_the_upload_scope()
    {
        await using WebApplication app = BuildApp(
            new FakeUploadAuthorizationPort(),
            new FakeUploadApplicationPort());
        RouteEndpoint[] endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Equal(6, endpoints.Length);
        Assert.All(endpoints, endpoint =>
        {
            IAuthorizeData authorization =
                Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(UploadEndpointMapping.UploadPolicyName, authorization.Policy);
        });
        RouteEndpoint content = Assert.Single(
            endpoints,
            endpoint => endpoint.RoutePattern.RawText ==
                "/api/v1/uploads/{id:guid}/content");
        IRequestSizeLimitMetadata requestLimit =
            Assert.IsAssignableFrom<IRequestSizeLimitMetadata>(
                content.Metadata.GetMetadata<IRequestSizeLimitMetadata>());
        Assert.Equal(
            UploadEndpointMapping.MaximumProxyRequestBodyBytes,
            requestLimit.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(false, false, true, false, true, 25_000_000, "proxy")]
    [InlineData(true, false, true, false, true, 25_000_000, "direct")]
    [InlineData(true, true, true, true, true, 4_000_000, "direct")]
    [InlineData(true, true, true, true, true, 25_000_000, "multipart")]
    [InlineData(true, true, false, false, false, 25_000_000, "proxy")]
    [InlineData(true, true, true, false, true, 25_000_000, "direct")]
    [InlineData(true, true, true, true, false, 25_000_000, "proxy")]
    public async Task Create_selects_strategy_from_provider_capabilities(
        bool direct,
        bool multipart,
        bool conditionalCreate,
        bool conditionalMultipart,
        bool sha256,
        long sizeBytes,
        string expectedStrategy)
    {
        var application = new FakeUploadApplicationPort
        {
            Policy = Policy(
                direct,
                multipart,
                conditionalCreate,
                conditionalMultipart,
                sha256),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(sizeBytes),
            idempotencyKey: "create-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expectedStrategy, response.JsonString("strategy"));
        Assert.Equal(expectedStrategy, application.LastReserve?.Strategy);
        Assert.Equal(
            $"staging/01/{TenantId:D}/{UploadId:D}",
            application.LastReserve?.StagingKey);
        Assert.DoesNotContain("staging/", response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(S3ProviderKind.Aws, "multipart")]
    [InlineData(S3ProviderKind.CloudflareR2, "proxy")]
    [InlineData(S3ProviderKind.BackblazeB2, "proxy")]
    [InlineData(S3ProviderKind.Minio, "direct")]
    public async Task S3_profiles_fall_back_from_unsupported_upload_operations(
        S3ProviderKind provider,
        string expectedStrategy)
    {
        S3ProviderProfile profile = S3ProviderProfiles.Get(provider);
        var application = new FakeUploadApplicationPort
        {
            Policy = new UploadProviderPolicy(
                profile.Capabilities,
                maximumUploadBytes: 50_000_000,
                multipartThresholdBytes: 10_000_000,
                planLifetime: TimeSpan.FromMinutes(5)),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(25_000_000),
            idempotencyKey: $"profile-{provider}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(expectedStrategy, response.JsonString("strategy"));
    }

    [Fact]
    public async Task Quota_is_reserved_before_any_upload_plan_is_created()
    {
        var application = new FakeUploadApplicationPort
        {
            ReserveResult = UploadReserveResult.QuotaExceeded(TimeSpan.FromSeconds(37)),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "quota-1");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("37", response.Headers.RetryAfter.ToString());
        Assert.Equal("upload_quota_exceeded", response.ProblemCode());
        Assert.Equal(0, application.IssueCalls);
    }

    [Theory]
    [InlineData("image/gif", Checksum, 1000, HttpStatusCode.UnprocessableEntity)]
    [InlineData("image/jpeg", "not-a-checksum", 1000, HttpStatusCode.UnprocessableEntity)]
    [InlineData("image/jpeg", Checksum, 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData("image/jpeg", Checksum, 60000000, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Invalid_declarations_are_rejected_before_quota_or_planning(
        string contentType,
        string checksum,
        long sizeBytes,
        HttpStatusCode expectedStatus)
    {
        var application = new FakeUploadApplicationPort();
        string body = JsonSerializer.Serialize(new
        {
            fileName = sizeBytes > 50_000_000
                ? "large.jpg"
                : "../../secret\r\nX-Evil: yes.jpg",
            sizeBytes,
            contentType,
            sha256 = checksum,
        });

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: body,
            idempotencyKey: "invalid-1");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(0, application.ReserveCalls);
        Assert.DoesNotContain("X-Evil", response.Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filename_is_display_metadata_only_and_never_affects_the_storage_key()
    {
        var application = new FakeUploadApplicationPort();
        string body = JsonSerializer.Serialize(new
        {
            fileName = "../../holiday-final.webp",
            sizeBytes = 1_000,
            contentType = "image/jpeg",
            sha256 = Checksum,
        });

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: body,
            idempotencyKey: "filename-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("../../holiday-final.webp", application.LastReserve?.DisplayFileName);
        Assert.DoesNotContain("holiday", application.LastReserve?.StagingKey, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", response.Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_replay_is_stable_and_changed_request_conflicts()
    {
        var application = new FakeUploadApplicationPort
        {
            ReserveResult = UploadReserveResult.Replayed(Snapshot()),
        };

        TestResponse replay = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "stable-create");
        application.ReserveResult = UploadReserveResult.Conflict();
        TestResponse conflict = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(2_000),
            idempotencyKey: "stable-create");

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_key_conflict", conflict.ProblemCode());
    }

    [Fact]
    public async Task Create_replay_does_not_issue_new_credentials_after_upload_started_processing()
    {
        var application = new FakeUploadApplicationPort
        {
            ReserveResult = UploadReserveResult.Replayed(Snapshot(
                state: "commitRequested",
                version: 3)),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "processed-create");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("commitRequested", response.JsonString("state"));
        Assert.False(response.Json().RootElement.TryGetProperty("plan", out _));
        Assert.Equal(0, application.IssueCalls);
    }

    [Fact]
    public async Task Unsafe_or_overlong_signed_plans_fail_closed_without_leaking_the_target()
    {
        var application = new FakeUploadApplicationPort
        {
            Issuance = UploadIssuance.Direct(
                Snapshot(),
                new UploadSignedRequest(
                    "PUT",
                    new Uri("https://storage.invalid/private?secret=credential"),
                    new Dictionary<string, string>
                    {
                        ["Authorization"] = "secret",
                    },
                    Now.AddMinutes(30))),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "unsafe-plan");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upload_service_unavailable", response.ProblemCode());
        Assert.DoesNotContain("credential", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_plan_headers_cannot_override_the_declared_content_type()
    {
        var application = new FakeUploadApplicationPort
        {
            Issuance = UploadIssuance.Direct(
                Snapshot(),
                new UploadSignedRequest(
                    "PUT",
                    new Uri("https://storage.invalid/opaque-target"),
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = "text/html",
                    },
                    Now.AddMinutes(5))),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "mismatched-plan");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upload_service_unavailable", response.ProblemCode());
    }

    [Fact]
    public async Task Signed_plan_preserves_exact_provider_condition_and_metadata_headers()
    {
        var headers = new Dictionary<string, string>
        {
            ["If-None-Match"] = "*",
            ["x-amz-meta-vistara-upload-id"] = UploadId.ToString("D"),
            ["x-ms-meta-vistara_m_74656e616e74"] = TenantId.ToString("D"),
        };
        var application = new FakeUploadApplicationPort
        {
            Issuance = UploadIssuance.Direct(
                Snapshot(),
                new UploadSignedRequest(
                    "PUT",
                    new Uri("https://storage.invalid/opaque-target"),
                    headers,
                    Now.AddMinutes(5))),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "provider-headers");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        JsonElement returned = response.Json().RootElement
            .GetProperty("plan")
            .GetProperty("request")
            .GetProperty("headers");
        Assert.Equal(headers.Count, returned.EnumerateObject().Count());
        Assert.All(
            headers,
            header => Assert.Equal(
                header.Value,
                returned.GetProperty(header.Key).GetString()));
    }

    [Theory]
    [InlineData("Authorization", "Bearer secret")]
    [InlineData("If-None-Match", "\"attacker-etag\"")]
    [InlineData("x-amz-meta-", "value")]
    [InlineData("x-ms-meta-safe", "value\r\nAuthorization: secret")]
    public async Task Signed_plan_rejects_generic_or_malformed_headers(
        string name,
        string value)
    {
        var application = new FakeUploadApplicationPort
        {
            Issuance = UploadIssuance.Direct(
                Snapshot(),
                new UploadSignedRequest(
                    "PUT",
                    new Uri("https://storage.invalid/opaque-target"),
                    new Dictionary<string, string>
                    {
                        [name] = value,
                    },
                    Now.AddMinutes(5))),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: CreateBody(1_000),
            idempotencyKey: "unsafe-provider-header");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("upload_service_unavailable", response.ProblemCode());
        Assert.DoesNotContain("secret", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_json_bodies_are_bounded_before_application_work()
    {
        var application = new FakeUploadApplicationPort();
        string body =
            $$"""
            {
              "fileName": "photo.jpg",
              "sizeBytes": 1000,
              "contentType": "image/jpeg",
              "sha256": "{{Checksum}}",
              "padding": "{{new string('a', 40_000)}}"
            }
            """;

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads",
            body: body,
            omitBodyContentLength: true,
            idempotencyKey: "chunked-json");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("upload_request_too_large", response.ProblemCode());
        Assert.Equal(0, application.ReserveCalls);
    }

    [Fact]
    public async Task Status_is_tenant_concealed_versioned_no_store_and_safe()
    {
        var application = new FakeUploadApplicationPort();
        TestResponse response = await SendAsync(
            application: application,
            method: "GET",
            route: "/api/v1/uploads/{id:guid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"v2\"", response.Headers.ETag.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("staging/", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("providerUpload", response.Body, StringComparison.OrdinalIgnoreCase);

        var concealed = new FakeUploadAuthorizationPort
        {
            SessionAccess = UploadAccess.Denied(UploadAccessStatus.Concealed),
        };
        TestResponse denied = await SendAsync(
            authorization: concealed,
            application: application,
            method: "GET",
            route: "/api/v1/uploads/{id:guid}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(1, application.GetCalls);
    }

    [Fact]
    public async Task Proxy_upload_streams_without_buffering_and_advances_the_version()
    {
        byte[] bytes = Enumerable.Range(0, 1_000).Select(value => (byte)value).ToArray();
        var source = new BoundedProbeStream(bytes, maximumRequestedRead: 64);
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(
                strategy: "proxy",
                expectedSizeBytes: bytes.Length,
                sha256: Sha256(bytes)),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: source,
            contentLength: bytes.Length,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("\"v3\"", response.Headers.ETag.ToString());
        Assert.Equal(bytes.Length, application.ProxyBytes);
        Assert.False(source.SynchronousReadUsed);
        Assert.True(source.MaximumObservedRead <= 64);
    }

    [Fact]
    public async Task Proxy_replay_is_evaluated_before_stale_if_match_and_validates_the_body()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("replayed proxy body");
        var source = new MemoryStream(bytes);
        UploadSessionSnapshot replayed = Snapshot(
            strategy: "proxy",
            version: 3,
            expectedSizeBytes: bytes.LongLength,
            sha256: Sha256(bytes));
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = replayed,
            ProxyResult = UploadWriteResult.Replayed(replayed),
            DisposeProxyStream = true,
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: source,
            contentLength: null,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("true", response.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal("\"v3\"", response.Headers.ETag.ToString());
        Assert.Equal(1, application.ProxyWriteCalls);
        Assert.Equal(2, application.LastProxyExpectedVersion);
        Assert.Equal(bytes.LongLength, source.Position);

        source = new MemoryStream(Encoding.UTF8.GetBytes("replayed proxy bodx"));
        TestResponse mismatch = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: source,
            contentLength: null,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
        Assert.Equal("upload_integrity_failed", mismatch.ProblemCode());
        Assert.Equal(2, application.ProxyWriteCalls);
        Assert.Equal(source.Length, source.Position);
    }

    [Fact]
    public async Task Issuance_version_is_not_treated_as_a_proxy_replay_receipt()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("first proxy body");
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(
                strategy: "proxy",
                version: 2,
                expectedSizeBytes: bytes.LongLength,
                sha256: Sha256(bytes)),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: new MemoryStream(bytes),
            contentLength: null,
            contentType: "image/jpeg",
            ifMatch: "\"v1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("upload_version_conflict", response.ProblemCode());
        Assert.Equal(1, application.ProxyWriteCalls);
    }

    [Fact]
    public async Task Proxy_upload_rejects_declared_and_streamed_overflow()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "proxy", expectedSizeBytes: 3),
        };
        TestResponse declared = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: new MemoryStream([1, 2, 3, 4]),
            contentLength: 4,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");
        TestResponse streamed = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: new MemoryStream([1, 2, 3, 4]),
            contentLength: null,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
    }

    [Fact]
    public async Task Proxy_upload_requires_the_upload_issued_state()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "proxy", state: "pending"),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "PUT",
            route: "/api/v1/uploads/{id:guid}/content",
            requestStream: new MemoryStream(new byte[1_000]),
            contentLength: 1_000,
            contentType: "image/jpeg",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("upload_invalid_state", response.ProblemCode());
    }

    [Fact]
    public async Task Proxy_upload_forwards_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "proxy", expectedSizeBytes: 3),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                application: application,
                method: "PUT",
                route: "/api/v1/uploads/{id:guid}/content",
                requestStream: new MemoryStream([1, 2, 3]),
                contentLength: 3,
                contentType: "image/jpeg",
                ifMatch: "\"v2\"",
                cancellationToken: cancellation.Token));
        Assert.True(application.CancellationObserved);
    }

    [Fact]
    public async Task Multipart_refresh_requires_active_owned_session_and_unique_valid_parts()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "multipart"),
        };
        TestResponse valid = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/parts",
            body: """{"partNumbers":[1,3]}""",
            ifMatch: "\"v2\"");
        TestResponse duplicate = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/parts",
            body: """{"partNumbers":[1,1]}""",
            ifMatch: "\"v2\"");
        application.SnapshotValue = Snapshot(strategy: "multipart", state: "expired");
        TestResponse expired = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/parts",
            body: """{"partNumbers":[1]}""",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(2, valid.Json().RootElement.GetProperty("parts").GetArrayLength());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Fact]
    public async Task Multipart_refresh_fails_closed_if_storage_returns_an_unrequested_part()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "multipart"),
            ReturnedPartNumbers = [2],
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/parts",
            body: """{"partNumbers":[1]}""",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("opaque-part-2", response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"parts":[{"partNumber":2,"etag":"e2","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":500},{"partNumber":1,"etag":"e1","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":500}]}""")]
    [InlineData("""{"parts":[{"partNumber":1,"etag":"e1","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":500},{"partNumber":1,"etag":"e2","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":500}]}""")]
    [InlineData("""{"parts":[{"partNumber":1,"etag":"e1","checksum":"bad","sizeBytes":1000}]}""")]
    public async Task Multipart_commit_rejects_unordered_duplicate_or_invalid_parts(string body)
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "multipart"),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: body,
            idempotencyKey: "commit-invalid",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, application.CommitCalls);
    }

    [Fact]
    public async Task Commit_is_idempotent_versioned_and_never_accepts_client_observations()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(strategy: "multipart"),
            CommitResult = UploadCommitResult.Queued(Snapshot(
                strategy: "multipart",
                state: "commitRequested",
                version: 3)),
        };
        const string body =
            """{"parts":[{"partNumber":1,"etag":"etag-1","checksum":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sizeBytes":1000}]}""";

        TestResponse queued = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: body,
            idempotencyKey: "commit-1",
            ifMatch: "\"v2\"");
        application.CommitResult = UploadCommitResult.Replayed(Snapshot(
            strategy: "multipart",
            state: "commitRequested",
            version: 3));
        application.SnapshotValue = Snapshot(strategy: "multipart", version: 3);
        TestResponse replay = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: body,
            idempotencyKey: "commit-1",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal("\"v3\"", queued.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
        Assert.Equal(2, application.LastCommitExpectedVersion);
        Assert.DoesNotContain("dimensions", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contentType", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Commit_idempotency_conflict_precedes_stale_if_match()
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(version: 3),
            CommitResult = UploadCommitResult.Failure(
                UploadCommitStatus.IdempotencyConflict),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: """{"parts":[]}""",
            idempotencyKey: "changed-commit",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_key_conflict", response.ProblemCode());
        Assert.Equal(2, application.LastCommitExpectedVersion);
    }

    [Fact]
    public async Task Mutation_results_cannot_switch_to_another_upload()
    {
        var application = new FakeUploadApplicationPort
        {
            CommitResult = UploadCommitResult.Queued(Snapshot(
                state: "commitRequested",
                version: 3) with
            {
                UploadId = OtherUploadId,
            }),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: """{"parts":[]}""",
            idempotencyKey: "scope-switch",
            ifMatch: "\"v2\"");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain(OtherUploadId.ToString("D"), response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UploadCommitStatus.IdempotencyConflict, HttpStatusCode.Conflict, "idempotency_key_conflict")]
    [InlineData(UploadCommitStatus.VersionConflict, HttpStatusCode.PreconditionFailed, "upload_version_conflict")]
    [InlineData(UploadCommitStatus.OutcomeUnknown, HttpStatusCode.Conflict, "upload_outcome_unknown")]
    public async Task Commit_conflicts_and_ambiguity_are_safe(
        UploadCommitStatus status,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var application = new FakeUploadApplicationPort
        {
            SnapshotValue = Snapshot(),
            CommitResult = UploadCommitResult.Failure(status),
        };

        TestResponse response = await SendAsync(
            application: application,
            method: "POST",
            route: "/api/v1/uploads/{id:guid}/commit",
            body: """{"parts":[]}""",
            idempotencyKey: "commit-conflict",
            ifMatch: "\"v2\"");

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, response.ProblemCode());
        Assert.DoesNotContain("staging/", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mutations_require_if_match_and_abort_is_idempotent()
    {
        var application = new FakeUploadApplicationPort();
        TestResponse missing = await SendAsync(
            application: application,
            method: "DELETE",
            route: "/api/v1/uploads/{id:guid}");
        TestResponse nonCanonical = await SendAsync(
            application: application,
            method: "DELETE",
            route: "/api/v1/uploads/{id:guid}",
            ifMatch: "\"v02\"");
        TestResponse aborted = await SendAsync(
            application: application,
            method: "DELETE",
            route: "/api/v1/uploads/{id:guid}",
            ifMatch: "\"v2\"");
        application.AbortResult = UploadAbortResult.AlreadyAborted(Snapshot(
            state: "aborted",
            version: 3));
        application.SnapshotValue = Snapshot(state: "aborted", version: 3);
        TestResponse replay = await SendAsync(
            application: application,
            method: "DELETE",
            route: "/api/v1/uploads/{id:guid}",
            ifMatch: "\"v3\"");

        Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, nonCanonical.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, aborted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal("true", replay.Headers["Idempotency-Replayed"].ToString());
    }

    private static UploadProviderPolicy Policy(
        bool direct = true,
        bool multipart = false,
        bool conditionalCreate = true,
        bool conditionalMultipart = true,
        bool sha256 = true) =>
        new(
            new BlobStoreCapabilities
            {
                SupportsDirectUpload = direct,
                SupportsMultipartUpload = multipart,
                SupportsConditionalCreate = conditionalCreate,
                SupportsConditionalMultipartCompletion = conditionalMultipart,
                NativeChecksumAlgorithms = sha256
                    ? [BlobChecksumAlgorithm.Sha256]
                    : [BlobChecksumAlgorithm.Crc64Nvme],
                Limits = new BlobStoreLimits(
                    100_000_000,
                    1_024,
                    10_000,
                    5_000_000,
                    50_000_000),
            },
            maximumUploadBytes: 50_000_000,
            multipartThresholdBytes: 10_000_000,
            planLifetime: TimeSpan.FromMinutes(5));

    private static UploadSessionSnapshot Snapshot(
        string strategy = "direct",
        string state = "uploadIssued",
        long version = 2,
        long expectedSizeBytes = 1_000,
        string sha256 = Checksum) =>
        new(
            TenantId,
            ActorId,
            UploadId,
            strategy,
            state,
            expectedSizeBytes,
            "image/jpeg",
            sha256,
            "../../display.jpg",
            $"staging/01/{TenantId:D}/{UploadId:D}",
            Now.AddHours(1),
            version,
            []);

    private static string CreateBody(long sizeBytes) => JsonSerializer.Serialize(new
    {
        fileName = "photo.jpg",
        sizeBytes,
        contentType = "image/jpeg",
        sha256 = Checksum,
    });

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static WebApplication BuildApp(
        IUploadAuthorizationPort authorization,
        IUploadApplicationPort application)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(
                UploadEndpointMapping.UploadPolicyName,
                policy => policy.RequireAuthenticatedUser().RequireClaim(
                    "scope",
                    "assets.upload")));
        builder.Services.AddScoped(_ => authorization);
        builder.Services.AddScoped(_ => application);
        builder.Services.AddSingleton<IClock>(new FakeClock(Now));
        builder.Services.AddSingleton<IUuid7Generator>(new FakeUuid7Generator(UploadId));
        WebApplication app = builder.Build();
        app.MapVistaraUploads();
        return app;
    }

    private static async Task<TestResponse> SendAsync(
        string method,
        string route,
        FakeUploadApplicationPort application,
        IUploadAuthorizationPort? authorization = null,
        string? body = null,
        Stream? requestStream = null,
        long? contentLength = null,
        string? contentType = null,
        string? idempotencyKey = null,
        string? ifMatch = null,
        bool omitBodyContentLength = false,
        CancellationToken cancellationToken = default)
    {
        await using WebApplication app = BuildApp(
            authorization ?? new FakeUploadAuthorizationPort(),
            application);
        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                    .Contains(method, StringComparer.Ordinal));
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
        };
        context.TraceIdentifier = "trace-uploads";
        context.Request.Method = method;
        context.Request.RouteValues["id"] = UploadId.ToString("D");
        context.Response.Body = new MemoryStream();
        if (body is not null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength =
                omitBodyContentLength ? null : bytes.Length;
            context.Request.ContentType = "application/json";
        }
        else if (requestStream is not null)
        {
            context.Request.Body = requestStream;
            context.Request.ContentLength = contentLength;
            context.Request.ContentType = contentType;
        }

        if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }

        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        await endpoint.RequestDelegate!(context);
        context.Response.Body.Position = 0;
        string responseBody = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.Headers,
            responseBody);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        IHeaderDictionary Headers,
        string Body)
    {
        public JsonDocument Json() => JsonDocument.Parse(Body);

        public string JsonString(string property) =>
            Json().RootElement.GetProperty(property).GetString()!;

        public string ProblemCode() => JsonString("code");
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeUuid7Generator(Guid value) : IUuid7Generator
    {
        public Guid NewId() => value;
    }

    private sealed class BoundedProbeStream(byte[] bytes, int maximumRequestedRead) : Stream
    {
        private int _position;

        public bool SynchronousReadUsed { get; private set; }

        public int MaximumObservedRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SynchronousReadUsed = true;
            throw new InvalidOperationException("Synchronous reads are forbidden.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumObservedRead = Math.Max(MaximumObservedRead, buffer.Length);
            if (buffer.Length > maximumRequestedRead)
            {
                throw new InvalidOperationException("The upload was read in an oversized chunk.");
            }

            int count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

internal sealed class FakeUploadAuthorizationPort : IUploadAuthorizationPort
{
    public UploadAccess CreateAccess { get; init; } =
        UploadAccess.Authorized(
            UploadEndpointContractTests.TenantIdForFakes,
            UploadEndpointContractTests.ActorIdForFakes);
    public UploadAccess SessionAccess { get; init; } =
        UploadAccess.Authorized(
            UploadEndpointContractTests.TenantIdForFakes,
            UploadEndpointContractTests.ActorIdForFakes);

    public ValueTask<UploadAccess> AuthorizeCreateAsync(
        HttpContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CreateAccess);

    public ValueTask<UploadAccess> AuthorizeSessionAsync(
        HttpContext context,
        Guid uploadId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(SessionAccess);
}

internal sealed class FakeUploadApplicationPort : IUploadApplicationPort
{
    public UploadProviderPolicy Policy { get; init; } =
        UploadEndpointContractTests.PolicyForFakes();
    public UploadReserveResult ReserveResult { get; set; } =
        UploadReserveResult.Created(UploadEndpointContractTests.SnapshotForFakes());
    public UploadIssuance Issuance { get; init; } =
        UploadIssuance.Direct(
            UploadEndpointContractTests.SnapshotForFakes(),
            new UploadSignedRequest(
                "PUT",
                new Uri("https://storage.invalid/opaque-target"),
                new Dictionary<string, string>
                {
                    ["Content-Type"] = "image/jpeg",
                    ["x-amz-checksum-sha256"] =
                        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                },
                UploadEndpointContractTests.NowForFakes.AddMinutes(5)));
    public UploadSessionSnapshot SnapshotValue { get; set; } =
        UploadEndpointContractTests.SnapshotForFakes();
    public UploadCommitResult CommitResult { get; set; } =
        UploadCommitResult.Queued(UploadEndpointContractTests.SnapshotForFakes(
            state: "commitRequested",
            version: 3));
    public UploadAbortResult AbortResult { get; set; } =
        UploadAbortResult.Aborted(UploadEndpointContractTests.SnapshotForFakes(
            state: "aborted",
            version: 3));
    public int ReserveCalls { get; private set; }
    public int IssueCalls { get; private set; }
    public int GetCalls { get; private set; }
    public int CommitCalls { get; private set; }
    public int ProxyWriteCalls { get; private set; }
    public long? LastCommitExpectedVersion { get; private set; }
    public long? LastProxyExpectedVersion { get; private set; }
    public long ProxyBytes { get; private set; }
    public bool CancellationObserved { get; private set; }
    public ReserveUploadRequest? LastReserve { get; private set; }
    public IReadOnlyList<int>? ReturnedPartNumbers { get; init; }
    public UploadWriteResult? ProxyResult { get; init; }
    public bool DisposeProxyStream { get; init; }

    public ValueTask<UploadProviderPolicy> GetProviderPolicyAsync(
        Guid tenantId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Policy);

    public ValueTask<UploadReserveResult> ReserveAsync(
        ReserveUploadRequest request,
        CancellationToken cancellationToken)
    {
        ReserveCalls++;
        LastReserve = request;
        if (ReserveResult.Status == UploadReserveStatus.Created)
        {
            return ValueTask.FromResult(UploadReserveResult.Created(
                SnapshotValue with
                {
                    TenantId = request.TenantId,
                    ActorId = request.ActorId,
                    UploadId = request.UploadId,
                    Strategy = request.Strategy,
                    DisplayFileName = request.DisplayFileName,
                    ExpectedSizeBytes = request.ExpectedSizeBytes,
                    DeclaredContentType = request.DeclaredContentType,
                    Sha256 = request.Sha256,
                    StagingKey = request.StagingKey,
                    ExpiresAtUtc = request.ExpiresAtUtc,
                }));
        }

        return ValueTask.FromResult(ReserveResult);
    }

    public ValueTask<UploadIssuance> IssueAsync(
        UploadSessionSnapshot session,
        CancellationToken cancellationToken)
    {
        IssueCalls++;
        if (session.Strategy == "proxy")
        {
            return ValueTask.FromResult(UploadIssuance.Proxy(session));
        }

        if (session.Strategy == "multipart")
        {
            BlobStoreLimits limits = Policy.Capabilities.Limits;
            return ValueTask.FromResult(UploadIssuance.Multipart(
                session,
                [
                    PartPlan(
                        1,
                        limits.MinMultipartPartBytes,
                        limits.MaxMultipartPartBytes),
                ],
                limits.MaxMultipartParts,
                limits.MinMultipartPartBytes,
                limits.MaxMultipartPartBytes));
        }

        return ValueTask.FromResult(UploadIssuance.Direct(
            session,
            Issuance.DirectRequest!));
    }

    public ValueTask<UploadSessionSnapshot?> GetAsync(
        Guid tenantId,
        Guid uploadId,
        CancellationToken cancellationToken)
    {
        GetCalls++;
        return ValueTask.FromResult<UploadSessionSnapshot?>(SnapshotValue);
    }

    public async ValueTask<UploadWriteResult> WriteProxyAsync(
        UploadSessionSnapshot session,
        Stream content,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ProxyWriteCalls++;
        LastProxyExpectedVersion = expectedVersion;
        if (ProxyResult is not null)
        {
            if (DisposeProxyStream)
            {
                content.Dispose();
            }

            return ProxyResult;
        }

        if (expectedVersion != session.Version)
        {
            return UploadWriteResult.Failure(UploadWriteStatus.VersionConflict);
        }

        byte[] buffer = new byte[64];
        while (true)
        {
            try
            {
                int read = await content.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                ProxyBytes += read;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        return UploadWriteResult.Written(session with { Version = expectedVersion + 1 });
    }

    public ValueTask<UploadPartPlanResult> RefreshPartPlansAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<int> partNumbers,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(UploadPartPlanResult.Created(
            (ReturnedPartNumbers ?? partNumbers)
                .Select(partNumber => PartPlan(
                    partNumber,
                    Policy.Capabilities.Limits.MinMultipartPartBytes,
                    Policy.Capabilities.Limits.MaxMultipartPartBytes))
                .ToArray()));

    public ValueTask<UploadCommitResult> CommitAsync(
        UploadSessionSnapshot session,
        IReadOnlyList<CommittedUploadPart> parts,
        IdempotencyKey idempotencyKey,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        CommitCalls++;
        LastCommitExpectedVersion = expectedVersion;
        return ValueTask.FromResult(CommitResult);
    }

    public ValueTask<UploadAbortResult> AbortAsync(
        UploadSessionSnapshot session,
        long expectedVersion,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(AbortResult);

    private static UploadSignedPartRequest PartPlan(
        int partNumber,
        long minBytes,
        long maxBytes) =>
        new(
            partNumber,
            new UploadSignedRequest(
                "PUT",
                new Uri($"https://storage.invalid/opaque-part-{partNumber}"),
                new Dictionary<string, string>(),
                UploadEndpointContractTests.NowForFakes.AddMinutes(5)),
            minBytes,
            maxBytes);
}
