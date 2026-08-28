using Vistara.Domain.Assets;
using Vistara.Domain.Uploads;

namespace Vistara.UnitTests.Uploads;

public sealed class UploadIntegrityTests
{
    private static readonly Sha256Checksum ExpectedChecksum = new(new string('d', 64));
    private static readonly UploadIntegrityExpectation Expected = new(
        4096,
        ExpectedChecksum,
        new MediaContentType("image/jpeg"));

    [Fact]
    public void Uploads_matching_observed_object_satisfies_integrity_expectations()
    {
        ObservedUploadObject observed = new(
            4096,
            new Sha256Checksum(new string('D', 64)),
            new MediaContentType("IMAGE/JPEG"),
            "staging/01/tenant/upload",
            "provider-version");

        Assert.True(Expected.Validate(observed).IsSuccess);
    }

    [Theory]
    [InlineData(4095, "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", "image/jpeg", "uploads.size_mismatch")]
    [InlineData(4096, "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "image/jpeg", "uploads.checksum_mismatch")]
    [InlineData(4096, "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd", "image/png", "uploads.content_type_mismatch")]
    public void Uploads_mismatched_integrity_fails_explicitly(
        long size,
        string checksum,
        string contentType,
        string expectedCode)
    {
        ObservedUploadObject observed = new(
            size,
            new Sha256Checksum(checksum),
            new MediaContentType(contentType),
            "staging/01/tenant/upload",
            "provider-version");

        var result = Expected.Validate(observed);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error?.Code);
    }
}
