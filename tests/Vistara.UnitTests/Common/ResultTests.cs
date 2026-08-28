using Vistara.Domain.Common;

namespace Vistara.UnitTests.Common;

public sealed class ResultTests
{
    [Fact]
    public void Success_has_no_error()
    {
        Result result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_exposes_the_expected_error_without_throwing()
    {
        ResultError error = ResultError.Conflict(
            "assets.version_conflict",
            "The asset was changed.");

        Result result = Result.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
    }

    [Fact]
    public void Generic_success_exposes_its_value()
    {
        Result<string> result = Result.Success("asset-1");

        bool hasValue = result.TryGetValue(out string? value);

        Assert.True(result.IsSuccess);
        Assert.True(hasValue);
        Assert.Equal("asset-1", value);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Generic_failure_exposes_error_and_no_value_without_throwing()
    {
        ResultError error = ResultError.NotFound(
            "assets.not_found",
            "The asset was not found.");
        Result<string> result = Result.Failure<string>(error);

        bool hasValue = result.TryGetValue(out string? value);

        Assert.True(result.IsFailure);
        Assert.False(hasValue);
        Assert.Null(value);
        Assert.Same(error, result.Error);
    }
}
