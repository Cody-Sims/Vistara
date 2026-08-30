using System.Text.Json;
using Vistara.Contracts.Capabilities;
using Xunit;

namespace Vistara.Api.ContractTests.Capabilities;

public sealed class CapabilitiesSerializationTests
{
    [Fact]
    public void Response_has_a_stable_versioned_camel_case_shape()
    {
        var response = new CapabilitiesResponse(
            1,
            new DatabaseCapabilitiesResponse("postgresql"),
            new StorageCapabilitiesResponse(
                "aws-s3",
                true,
                true,
                true,
                5_000,
                10_000,
                5,
                500),
            new ImagingCapabilitiesResponse(
                "net-vips",
                ["jpeg", "png", "webp"],
                ["jpeg", "png", "webp"],
                50_000,
                20_000,
                20_000,
                40_000_000,
                1,
                512_000,
                30,
                1),
            new UploadCapabilitiesResponse(
                50_000,
                3,
                false,
                16_000,
                true,
                true,
                true),
            new SearchCapabilitiesResponse(true, true, true, false),
            new ApiCapabilitiesResponse(60, 200, 50_000));

        string json = JsonSerializer.Serialize(response);

        Assert.Equal(
            """{"schemaVersion":1,"database":{"provider":"postgresql"},"storage":{"provider":"aws-s3","directUpload":true,"multipartUpload":true,"rangeReads":true,"maxObjectBytes":5000,"maxMultipartParts":10000,"minMultipartPartBytes":5,"maxMultipartPartBytes":500},"imaging":{"provider":"net-vips","inputFormats":["jpeg","png","webp"],"outputFormats":["jpeg","png","webp"],"maxEncodedBytes":50000,"maxWidth":20000,"maxHeight":20000,"maxAggregatePixels":40000000,"maxFrames":1,"maxEstimatedDecodedBytes":512000,"processingDeadlineSeconds":30,"maxConcurrentTransforms":1},"upload":{"maxBytes":50000,"maxConcurrentUploads":3,"concurrencyUnlimited":false,"multipartThresholdBytes":16000,"proxyUpload":true,"directUpload":true,"multipartUpload":true},"search":{"text":true,"facets":true,"timeline":true,"providerNativeFullText":false},"api":{"defaultPageSize":60,"maxPageSize":200,"maxProxyUploadBytes":50000}}""",
            json);
    }
}
