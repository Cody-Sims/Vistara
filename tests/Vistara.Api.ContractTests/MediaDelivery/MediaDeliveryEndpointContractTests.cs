using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Media;
using Vistara.Application.Common.Storage;
using Vistara.Contracts.Media;
using Xunit;

namespace Vistara.Api.ContractTests.MediaDelivery;

public sealed class MediaDeliveryEndpointContractTests
{
    private const string SourceHash =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RecipeHash =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Sha256 =
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string PublicRoute =
        "/media/{pipeline}/{sourceHash}/{recipeHash}.{extension}";
    private const string PrivateRoute =
        "/delivery/{pipeline}/{sourceHash}/{recipeHash}.{extension}";
    private const string AssetRenditionRoute =
        "/delivery/assets/{assetId:guid}/{renditionId:guid}";
    private const string OriginalRoute =
        "/api/v1/assets/{assetId:guid}/original";
    private static readonly Guid TenantId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000301");
    private static readonly Guid AssetId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000302");
    private static readonly Guid RenditionId =
        Guid.Parse("01990a2a-bc00-7000-8000-000000000303");
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("0123456789");

    [Fact]
    public async Task Public_ready_derivative_streams_exact_immutable_representation()
    {
        var source = new FakeMediaContentSource(Content);
        var application = FakeMediaApplicationPort.PublicReady(
            Representation(source, "image/webp"));

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            PublicRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", response.BodyText);
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal(Content.Length, response.ContentLength);
        Assert.Equal($"\"{Sha256}\"", response.Headers.ETag.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.PublicImmutableCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.False(response.Headers.ContainsKey("Vary"));
        Assert.Equal(1, source.OpenCalls);
        Assert.Null(source.LastRange);
    }

    [Fact]
    public async Task Head_has_get_header_parity_without_opening_or_writing_content()
    {
        var getSource = new FakeMediaContentSource(Content);
        var headSource = new FakeMediaContentSource(Content);

        TestResponse get = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(getSource, "image/webp")),
            "GET",
            PublicRoute);
        TestResponse head = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(headSource, "image/webp")),
            "HEAD",
            PublicRoute);

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.ContentType, head.ContentType);
        Assert.Equal(get.ContentLength, head.ContentLength);
        Assert.Equal(get.Headers.ETag.ToString(), head.Headers.ETag.ToString());
        Assert.Equal(
            get.Headers.CacheControl.ToString(),
            head.Headers.CacheControl.ToString());
        Assert.Equal(
            get.Headers.AcceptRanges.ToString(),
            head.Headers.AcceptRanges.ToString());
        Assert.Empty(head.Body);
        Assert.Equal(0, headSource.OpenCalls);
    }

    [Theory]
    [InlineData("\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"")]
    [InlineData("W/\"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"")]
    [InlineData("*")]
    public async Task If_none_match_returns_304_without_opening_content(string condition)
    {
        var source = new FakeMediaContentSource(Content);

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["If-None-Match"] = condition,
            });

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal($"\"{Sha256}\"", response.Headers.ETag.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.PublicImmutableCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal(0, source.OpenCalls);
    }

    [Fact]
    public async Task If_match_uses_strong_comparison_and_precedes_if_none_match()
    {
        var source = new FakeMediaContentSource(Content);

        TestResponse weak = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = $"W/\"{Sha256}\"",
                ["If-None-Match"] = "\"different\"",
            });
        TestResponse matching = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(new FakeMediaContentSource(Content), "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["If-Match"] = $"\"{Sha256}\"",
            });

        Assert.Equal(HttpStatusCode.PreconditionFailed, weak.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            weak.Headers.CacheControl.ToString());
        Assert.DoesNotContain(
            "immutable",
            weak.Headers.CacheControl.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(weak.Body);
        Assert.Equal(0, source.OpenCalls);
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
    }

    [Theory]
    [InlineData("bytes=2-5", "2345", "bytes 2-5/10", 2, 4)]
    [InlineData("bytes=7-", "789", "bytes 7-9/10", 7, 3)]
    [InlineData("bytes=-3", "789", "bytes 7-9/10", 7, 3)]
    public async Task Single_ranges_return_206_and_open_only_the_requested_bytes(
        string range,
        string expectedBody,
        string expectedContentRange,
        long expectedOffset,
        long expectedLength)
    {
        var source = new FakeMediaContentSource(Content);

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = range,
            });

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(expectedBody, response.BodyText);
        Assert.Equal(expectedContentRange, response.Headers.ContentRange.ToString());
        Assert.Equal(expectedLength, response.ContentLength);
        Assert.Equal(new MediaByteRange(expectedOffset, expectedLength), source.LastRange);
    }

    [Fact]
    public async Task If_range_applies_only_for_an_exact_strong_etag_match()
    {
        TestResponse matching = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(new FakeMediaContentSource(Content), "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=2-5",
                ["If-Range"] = $"\"{Sha256}\"",
            });
        TestResponse weak = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(new FakeMediaContentSource(Content), "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=2-5",
                ["If-Range"] = $"W/\"{Sha256}\"",
            });
        TestResponse stale = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(new FakeMediaContentSource(Content), "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=2-5",
                ["If-Range"] = "\"different\"",
            });

        Assert.Equal(HttpStatusCode.PartialContent, matching.StatusCode);
        Assert.Equal("2345", matching.BodyText);
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        Assert.Equal("0123456789", weak.BodyText);
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal("0123456789", stale.BodyText);
    }

    [Theory]
    [InlineData("bytes=20-30")]
    [InlineData("bytes=5-4")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("items=0-1")]
    public async Task Invalid_or_unsatisfiable_ranges_return_416_without_opening(
        string range)
    {
        var source = new FakeMediaContentSource(Content);

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = range,
            });

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal("bytes */10", response.Headers.ContentRange.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.DoesNotContain(
            "immutable",
            response.Headers.CacheControl.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, response.ContentLength);
        Assert.Empty(response.Body);
        Assert.Equal(0, source.OpenCalls);
    }

    [Fact]
    public async Task Queued_derivative_returns_a_no_store_202_contract_without_transforming()
    {
        var application = new FakeMediaApplicationPort
        {
            PublicResult = MediaDeliveryResult.Queued(),
        };

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            PublicRoute);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal(
            "queued",
            response.Json().RootElement.GetProperty("state").GetString());
        Assert.Equal(1, application.PublicResolveCalls);
    }

    [Fact]
    public async Task Private_derivatives_require_authorization_and_are_never_public_cacheable()
    {
        var source = new FakeMediaContentSource(Content);
        var application = new FakeMediaApplicationPort
        {
            PrivateResult = MediaDeliveryResult.Ready(
                Representation(source, "image/webp")),
        };

        TestResponse authorized = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            PrivateRoute);
        TestResponse concealed = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                PrivateAccess = MediaDeliveryAccess.Denied(
                    MediaDeliveryAccessStatus.Concealed),
            },
            application,
            "GET",
            PrivateRoute);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            authorized.Headers.CacheControl.ToString());
        Assert.Equal(HttpStatusCode.NotFound, concealed.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            concealed.Headers.CacheControl.ToString());
        Assert.Equal(1, application.PrivateResolveCalls);
        Assert.DoesNotContain("grant-token", concealed.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_delivery_reads_a_redacted_explicit_authorization_credential()
    {
        var authorization = new FakeMediaAuthorizationPort
        {
            PrivateAccess = MediaDeliveryAccess.Denied(
                MediaDeliveryAccessStatus.Concealed),
        };

        TestResponse response = await SendAsync(
            authorization,
            new FakeMediaApplicationPort(),
            "GET",
            PrivateRoute);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("grant-token", authorization.LastCredential?.PlaintextToken);
        Assert.Equal(
            MediaDeliveryHttpContract.RedactedCredential,
            authorization.LastCredential?.ToString());
        Assert.Equal(
            $"{MediaDeliveryHttpContract.DeliveryGrantAuthorizationScheme} grant-token",
            authorization.LastAuthorizationHeader);
        Assert.DoesNotContain(
            "grant-token",
            authorization.LastRequestPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            authorization.LastRouteValueNames,
            name => name.Contains("grant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("grant-token", response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Private_delivery_remains_viable_for_same_origin_cookie_authentication()
    {
        var authorization = new FakeMediaAuthorizationPort();

        TestResponse response = await SendAsync(
            authorization,
            new FakeMediaApplicationPort
            {
                PrivateResult = MediaDeliveryResult.Ready(
                    Representation(
                        new FakeMediaContentSource(Content),
                        "image/webp")),
            },
            "GET",
            PrivateRoute,
            deliveryCredential: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(authorization.LastCredential);
        Assert.Null(authorization.LastAuthorizationHeader);
        Assert.Equal(1, authorization.PrivateAuthorizeCalls);
    }

    [Fact]
    public async Task Other_authorization_schemes_are_not_reinterpreted_as_delivery_grants()
    {
        var authorization = new FakeMediaAuthorizationPort();

        TestResponse response = await SendAsync(
            authorization,
            new FakeMediaApplicationPort
            {
                PrivateResult = MediaDeliveryResult.Ready(
                    Representation(
                        new FakeMediaContentSource(Content),
                        "image/webp")),
            },
            "GET",
            PrivateRoute,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer authenticated-session-token",
            },
            deliveryCredential: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(authorization.LastCredential);
        Assert.Equal(1, authorization.PrivateAuthorizeCalls);
    }

    [Theory]
    [InlineData("Vistara-Delivery")]
    [InlineData("Vistara-Delivery ")]
    [InlineData("Vistara-Delivery token with spaces")]
    [InlineData("Vistara-Delivery \"quoted-token\"")]
    public async Task Malformed_delivery_authorization_is_concealed_without_port_calls(
        string authorizationHeader)
    {
        var authorization = new FakeMediaAuthorizationPort();

        TestResponse response = await SendAsync(
            authorization,
            new FakeMediaApplicationPort(),
            "GET",
            PrivateRoute,
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = authorizationHeader,
            },
            deliveryCredential: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal(0, authorization.PrivateAuthorizeCalls);
        Assert.DoesNotContain(
            authorizationHeader,
            response.BodyText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Original_requires_asset_authorization_and_conceals_cross_tenant_access()
    {
        var application = new FakeMediaApplicationPort
        {
            OriginalResult = MediaDeliveryResult.Ready(
                Representation(
                    new FakeMediaContentSource(Content),
                    "image/jpeg",
                    "holiday.jpg")),
        };

        TestResponse authorized = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            OriginalRoute);
        TestResponse concealed = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                OriginalAccess = MediaDeliveryAccess.AuthorizedAsset(
                    TenantId,
                    Guid.Parse("01990a2a-bc00-7000-8000-000000000399")),
            },
            application,
            "GET",
            OriginalRoute);

        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            authorized.Headers.CacheControl.ToString());
        Assert.Equal(
            "attachment; filename=\"holiday.jpg\"",
            authorized.Headers.ContentDisposition.ToString());
        Assert.Equal(HttpStatusCode.NotFound, concealed.StatusCode);
        Assert.Equal(1, application.OriginalResolveCalls);
    }

    [Fact]
    public async Task Asset_rendition_streams_ready_bytes_with_private_cache_and_no_topology_leakage()
    {
        var source = new FakeMediaContentSource(Content);
        var authorization = new FakeMediaAuthorizationPort();
        var application = new FakeMediaApplicationPort
        {
            AssetRenditionResult = MediaDeliveryResult.Ready(
                Representation(source, "image/webp")),
        };

        TestResponse response = await SendAsync(
            authorization,
            application,
            "GET",
            AssetRenditionRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("0123456789", response.BodyText);
        Assert.Equal("image/webp", response.ContentType);
        Assert.Equal($"\"{Sha256}\"", response.Headers.ETag.ToString());
        Assert.Equal(
            MediaDeliveryHttpContract.PrivateNoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.ToString());
        Assert.Equal("nosniff", response.Headers.XContentTypeOptions.ToString());
        Assert.False(response.Headers.ContainsKey("Content-Disposition"));
        Assert.Equal(1, authorization.AssetRenditionAuthorizeCalls);
        Assert.Equal(AssetId, authorization.LastAssetRenditionAssetId);
        Assert.Equal(TenantId, application.LastRenditionScope?.TenantId);
        Assert.Equal(AssetId, application.LastRenditionScope?.AssetId);
        Assert.Equal(RenditionId, application.LastRenditionScope?.RenditionId);
    }

    [Fact]
    public async Task Asset_rendition_serves_ranges_and_conditional_requests()
    {
        var rangeSource = new FakeMediaContentSource(Content);
        var conditionalSource = new FakeMediaContentSource(Content);

        TestResponse partial = await SendAsync(
            new FakeMediaAuthorizationPort(),
            new FakeMediaApplicationPort
            {
                AssetRenditionResult = MediaDeliveryResult.Ready(
                    Representation(rangeSource, "image/webp")),
            },
            "GET",
            AssetRenditionRoute,
            headers: new Dictionary<string, string>
            {
                ["Range"] = "bytes=2-5",
            });
        TestResponse notModified = await SendAsync(
            new FakeMediaAuthorizationPort(),
            new FakeMediaApplicationPort
            {
                AssetRenditionResult = MediaDeliveryResult.Ready(
                    Representation(conditionalSource, "image/webp")),
            },
            "GET",
            AssetRenditionRoute,
            headers: new Dictionary<string, string>
            {
                ["If-None-Match"] = $"\"{Sha256}\"",
            });

        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("2345", partial.BodyText);
        Assert.Equal("bytes 2-5/10", partial.Headers.ContentRange.ToString());
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Empty(notModified.Body);
        Assert.Equal(0, conditionalSource.OpenCalls);
    }

    [Fact]
    public async Task Asset_rendition_requires_authentication_and_conceals_other_denials()
    {
        var application = new FakeMediaApplicationPort
        {
            AssetRenditionResult = MediaDeliveryResult.Ready(
                Representation(new FakeMediaContentSource(Content), "image/webp")),
        };

        TestResponse unauthenticated = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                AssetRenditionAccess = MediaDeliveryAccess.Denied(
                    MediaDeliveryAccessStatus.Unauthenticated),
            },
            application,
            "GET",
            AssetRenditionRoute);
        TestResponse forbidden = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                AssetRenditionAccess = MediaDeliveryAccess.Denied(
                    MediaDeliveryAccessStatus.Forbidden),
            },
            application,
            "GET",
            AssetRenditionRoute);
        TestResponse concealed = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                AssetRenditionAccess = MediaDeliveryAccess.Denied(
                    MediaDeliveryAccessStatus.Concealed),
            },
            application,
            "GET",
            AssetRenditionRoute);
        TestResponse otherAsset = await SendAsync(
            new FakeMediaAuthorizationPort
            {
                AssetRenditionAccess = MediaDeliveryAccess.AuthorizedAsset(
                    TenantId,
                    Guid.Parse("01990a2a-bc00-7000-8000-000000000398")),
            },
            application,
            "GET",
            AssetRenditionRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal("authentication_required", ProblemCode(unauthenticated));
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal("media_not_found", ProblemCode(forbidden));
        Assert.Equal(HttpStatusCode.NotFound, concealed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherAsset.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            concealed.Headers.CacheControl.ToString());
        Assert.Equal(0, application.AssetRenditionResolveCalls);
    }

    [Fact]
    public async Task Asset_rendition_reports_unknown_as_not_found_and_pending_as_queued()
    {
        TestResponse missing = await SendAsync(
            new FakeMediaAuthorizationPort(),
            new FakeMediaApplicationPort
            {
                AssetRenditionResult = MediaDeliveryResult.NotFound(),
            },
            "GET",
            AssetRenditionRoute);
        TestResponse queued = await SendAsync(
            new FakeMediaAuthorizationPort(),
            new FakeMediaApplicationPort
            {
                AssetRenditionResult = MediaDeliveryResult.Queued(),
            },
            "GET",
            AssetRenditionRoute);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("media_not_found", ProblemCode(missing));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            queued.Headers.CacheControl.ToString());
        Assert.Equal(
            "queued",
            queued.Json().RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Original_filename_is_sanitized_without_header_injection()
    {
        var application = new FakeMediaApplicationPort
        {
            OriginalResult = MediaDeliveryResult.Ready(
                Representation(
                    new FakeMediaContentSource(Content),
                    "image/jpeg",
                    "../../evil\"\r\nX-Injected: yes.jpg")),
        };

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            OriginalRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "attachment; filename=\"evil___X-Injected_ yes.jpg\"",
            response.Headers.ContentDisposition.ToString());
        Assert.False(response.Headers.ContainsKey("X-Injected"));
        Assert.DoesNotContain('\r', response.Headers.ContentDisposition.ToString());
        Assert.DoesNotContain('\n', response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Unsafe_media_types_fail_closed_with_a_safe_problem()
    {
        var application = FakeMediaApplicationPort.PublicReady(
            Representation(
                new FakeMediaContentSource(Content),
                "text/html"));

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            PublicRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal("media_service_unavailable", ProblemCode(response));
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("text/html", response.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dependency_failures_do_not_expose_object_keys_or_signed_urls()
    {
        var application = new FakeMediaApplicationPort
        {
            Exception = new BlobStoreException(
                BlobStoreErrorCode.OutcomeUnknown,
                "originals/private/object-key https://signed.example/secret"),
        };

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            application,
            "GET",
            OriginalRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("media_service_unavailable", ProblemCode(response));
        Assert.DoesNotContain("object-key", response.BodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("signed.example", response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_open_failures_replace_representation_headers_with_a_safe_problem()
    {
        var source = new FakeMediaContentSource(Content)
        {
            Exception = new IOException(
                "derivatives/v1/private-key https://signed.example/secret"),
        };

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.False(response.Headers.ContainsKey("ETag"));
        Assert.False(response.Headers.ContainsKey("Accept-Ranges"));
        Assert.DoesNotContain("private-key", response.BodyText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_stream_failures_do_not_retain_representation_headers()
    {
        var source = new FakeMediaContentSource(Content)
        {
            StreamFactory = _ => new FailingReadStream(
                "derivatives/v1/private-key https://signed.example/secret"),
        };

        TestResponse response = await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            MediaDeliveryHttpContract.NoStoreCacheControl,
            response.Headers.CacheControl.ToString());
        Assert.False(response.Headers.ContainsKey("ETag"));
        Assert.False(response.Headers.ContainsKey("Accept-Ranges"));
        Assert.Equal("application/problem+json", response.ContentType);
        Assert.Equal("media_service_unavailable", ProblemCode(response));
        Assert.DoesNotContain(
            "private-key",
            response.BodyText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_stream_failures_after_response_start_abort_without_rewriting_headers()
    {
        var responseFeature = new StartedResponseFeature();
        var lifetimeFeature = new TrackingRequestLifetimeFeature();
        var source = new FakeMediaContentSource(Content)
        {
            StreamFactory = _ => new FailingReadStream(
                "late stream failure",
                responseFeature.MarkStarted),
        };

        await SendAsync(
            new FakeMediaAuthorizationPort(),
            FakeMediaApplicationPort.PublicReady(
                Representation(source, "image/webp")),
            "GET",
            PublicRoute,
            configureContext: context =>
            {
                context.Features.Set<IHttpResponseFeature>(responseFeature);
                context.Features.Set<IHttpRequestLifetimeFeature>(
                    lifetimeFeature);
            });

        Assert.True(responseFeature.Started);
        Assert.True(lifetimeFeature.Aborted);
        Assert.Equal(
            MediaDeliveryHttpContract.PublicImmutableCacheControl,
            responseFeature.Headers.CacheControl.ToString());
        Assert.Equal($"\"{Sha256}\"", responseFeature.Headers.ETag.ToString());
        Assert.Equal(
            "image/webp",
            responseFeature.Headers.ContentType.ToString());
    }

    [Fact]
    public async Task Stream_copy_is_cancellation_aware_and_disposes_the_read_handle()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = new CancellationObservingStream();
        var source = new FakeMediaContentSource(Content)
        {
            StreamFactory = _ => stream,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(
                new FakeMediaAuthorizationPort(),
                FakeMediaApplicationPort.PublicReady(
                    Representation(source, "image/webp")),
                "GET",
                PublicRoute,
                cancellationToken: cancellation.Token));

        Assert.True(stream.CancellationTokenObserved);
        Assert.True(stream.Disposed);
    }

    private static MediaRepresentation Representation(
        IMediaContentSource source,
        string contentType,
        string? downloadFileName = null) =>
        new(
            Content.Length,
            contentType,
            Sha256,
            source,
            downloadFileName);

    private static string ProblemCode(TestResponse response) =>
        response.Json().RootElement.GetProperty("code").GetString()!;

    private static async Task<TestResponse> SendAsync(
        IMediaDeliveryAuthorizationPort authorization,
        IMediaDeliveryApplicationPort application,
        string method,
        string route,
        IReadOnlyDictionary<string, string>? headers = null,
        string? deliveryCredential = "grant-token",
        Action<DefaultHttpContext>? configureContext = null,
        CancellationToken cancellationToken = default)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped(_ => authorization);
        builder.Services.AddScoped(_ => application);
        await using WebApplication app = builder.Build();
        app.MapVistaraMedia();

        RouteEndpoint endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>(),
            candidate => candidate.RoutePattern.RawText == route);

        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            RequestAborted = cancellationToken,
        };
        context.Request.Method = method;
        context.Request.Path = route
            .Replace("{pipeline}", "v1", StringComparison.Ordinal)
            .Replace("{sourceHash}", SourceHash, StringComparison.Ordinal)
            .Replace("{recipeHash}", RecipeHash, StringComparison.Ordinal)
            .Replace("{extension}", "webp", StringComparison.Ordinal)
            .Replace(
                "{assetId:guid}",
                AssetId.ToString("D"),
                StringComparison.Ordinal)
            .Replace(
                "{renditionId:guid}",
                RenditionId.ToString("D"),
                StringComparison.Ordinal);
        context.Request.RouteValues["pipeline"] = "v1";
        context.Request.RouteValues["sourceHash"] = SourceHash;
        context.Request.RouteValues["recipeHash"] = RecipeHash;
        context.Request.RouteValues["extension"] = "webp";
        context.Request.RouteValues["assetId"] = AssetId.ToString("D");
        context.Request.RouteValues["renditionId"] = RenditionId.ToString("D");
        context.Response.Body = new MemoryStream();
        context.TraceIdentifier = "trace-media-delivery";
        configureContext?.Invoke(context);
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
            {
                context.Request.Headers[name] = value;
            }
        }

        if (route == PrivateRoute &&
            deliveryCredential is not null &&
            !context.Request.Headers.ContainsKey("Authorization"))
        {
            context.Request.Headers.Authorization =
                $"{MediaDeliveryHttpContract.DeliveryGrantAuthorizationScheme} " +
                deliveryCredential;
        }

        await endpoint.RequestDelegate!(context);
        byte[] responseBytes = ((MemoryStream)context.Response.Body).ToArray();
        return new TestResponse(
            (HttpStatusCode)context.Response.StatusCode,
            context.Response.ContentType,
            context.Response.ContentLength,
            context.Response.Headers,
            responseBytes);
    }

    private sealed record TestResponse(
        HttpStatusCode StatusCode,
        string? ContentType,
        long? ContentLength,
        IHeaderDictionary Headers,
        byte[] Body)
    {
        public string BodyText => Encoding.UTF8.GetString(Body);

        public JsonDocument Json() => JsonDocument.Parse(Body);
    }

    private sealed class FakeMediaAuthorizationPort : IMediaDeliveryAuthorizationPort
    {
        public MediaDeliveryAccess PrivateAccess { get; init; } =
            MediaDeliveryAccess.AuthorizedTenant(TenantId);

        public MediaDeliveryAccess OriginalAccess { get; init; } =
            MediaDeliveryAccess.AuthorizedAsset(TenantId, AssetId);

        public MediaDeliveryAccess AssetRenditionAccess { get; init; } =
            MediaDeliveryAccess.AuthorizedAsset(TenantId, AssetId);

        public int AssetRenditionAuthorizeCalls { get; private set; }

        public Guid? LastAssetRenditionAssetId { get; private set; }

        public int PrivateAuthorizeCalls { get; private set; }

        public MediaDeliveryCredential? LastCredential { get; private set; }

        public string? LastAuthorizationHeader { get; private set; }

        public string LastRequestPath { get; private set; } = string.Empty;

        public string[] LastRouteValueNames { get; private set; } = [];

        public ValueTask<MediaDeliveryAccess> AuthorizePrivateDerivativeAsync(
            HttpContext context,
            MediaDeliveryCredential? credential,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrivateAuthorizeCalls++;
            LastCredential = credential;
            LastAuthorizationHeader = context.Request.Headers.Authorization
                .FirstOrDefault();
            LastRequestPath = context.Request.Path.ToString();
            LastRouteValueNames = context.Request.RouteValues.Keys.ToArray();
            return ValueTask.FromResult(PrivateAccess);
        }

        public ValueTask<MediaDeliveryAccess> AuthorizeAssetRenditionAsync(
            HttpContext context,
            Guid assetId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetRenditionAuthorizeCalls++;
            LastAssetRenditionAssetId = assetId;
            LastRequestPath = context.Request.Path.ToString();
            return ValueTask.FromResult(AssetRenditionAccess);
        }

        public ValueTask<MediaDeliveryAccess> AuthorizeOriginalAsync(
            HttpContext context,
            Guid assetId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OriginalAccess);
    }

    private sealed class FakeMediaApplicationPort : IMediaDeliveryApplicationPort
    {
        public MediaDeliveryResult PublicResult { get; init; } =
            MediaDeliveryResult.NotFound();

        public MediaDeliveryResult PrivateResult { get; init; } =
            MediaDeliveryResult.NotFound();

        public MediaDeliveryResult OriginalResult { get; init; } =
            MediaDeliveryResult.NotFound();

        public MediaDeliveryResult AssetRenditionResult { get; init; } =
            MediaDeliveryResult.NotFound();

        public MediaRenditionScope? LastRenditionScope { get; private set; }

        public int AssetRenditionResolveCalls { get; private set; }

        public Exception? Exception { get; init; }

        public int PublicResolveCalls { get; private set; }

        public int PrivateResolveCalls { get; private set; }

        public int OriginalResolveCalls { get; private set; }

        public static FakeMediaApplicationPort PublicReady(
            MediaRepresentation representation) =>
            new()
            {
                PublicResult = MediaDeliveryResult.Ready(representation),
            };

        public ValueTask<MediaDeliveryResult> ResolvePublicDerivativeAsync(
            MediaDerivativeRequest request,
            CancellationToken cancellationToken)
        {
            PublicResolveCalls++;
            ThrowIfNeeded(cancellationToken);
            return ValueTask.FromResult(PublicResult);
        }

        public ValueTask<MediaDeliveryResult> ResolvePrivateDerivativeAsync(
            MediaTenantScope scope,
            MediaDerivativeRequest request,
            CancellationToken cancellationToken)
        {
            PrivateResolveCalls++;
            ThrowIfNeeded(cancellationToken);
            return ValueTask.FromResult(PrivateResult);
        }

        public ValueTask<MediaDeliveryResult> ResolveAssetRenditionAsync(
            MediaRenditionScope scope,
            CancellationToken cancellationToken)
        {
            AssetRenditionResolveCalls++;
            LastRenditionScope = scope;
            ThrowIfNeeded(cancellationToken);
            return ValueTask.FromResult(AssetRenditionResult);
        }

        public ValueTask<MediaDeliveryResult> ResolveOriginalAsync(
            MediaAssetScope scope,
            CancellationToken cancellationToken)
        {
            OriginalResolveCalls++;
            ThrowIfNeeded(cancellationToken);
            return ValueTask.FromResult(OriginalResult);
        }

        private void ThrowIfNeeded(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Exception is not null)
            {
                throw Exception;
            }
        }
    }

    private sealed class FakeMediaContentSource(byte[] content) : IMediaContentSource
    {
        public Func<MediaByteRange?, Stream>? StreamFactory { get; init; }

        public Exception? Exception { get; init; }

        public int OpenCalls { get; private set; }

        public MediaByteRange? LastRange { get; private set; }

        public async ValueTask<MediaReadHandle> OpenReadAsync(
            MediaByteRange? range,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            LastRange = range;
            if (Exception is not null)
            {
                throw Exception;
            }

            if (StreamFactory is not null)
            {
                return new MediaReadHandle(StreamFactory(range));
            }

            int offset = checked((int)(range?.Offset ?? 0));
            int length = checked((int)(range?.Length ?? content.Length));
            var copy = new byte[length];
            Array.Copy(content, offset, copy, 0, length);
            await Task.Yield();
            return new MediaReadHandle(new MemoryStream(copy, writable: false));
        }
    }

    private sealed class CancellationObservingStream : Stream
    {
        public bool CancellationTokenObserved { get; private set; }

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            CancellationTokenObserved = cancellationToken.CanBeCanceled;
            throw new OperationCanceledException(cancellationToken);
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
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream(
        string message,
        Action? beforeFailure = null) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            beforeFailure?.Invoke();
            throw new IOException(message);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            beforeFailure?.Invoke();
            return ValueTask.FromException<int>(new IOException(message));
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted => Started;

        public bool Started { get; private set; }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void MarkStarted() => Started = true;
    }

    private sealed class TrackingRequestLifetimeFeature :
        IHttpRequestLifetimeFeature
    {
        public CancellationToken RequestAborted { get; set; }

        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;
    }
}
