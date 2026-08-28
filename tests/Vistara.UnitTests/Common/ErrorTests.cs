using Vistara.Domain.Common;

namespace Vistara.UnitTests.Common;

public sealed class ErrorTests
{
    public static TheoryData<ErrorCategory, Func<string, string, ResultError>> Categories =>
        new()
        {
            { ErrorCategory.Validation, ResultError.Validation },
            { ErrorCategory.NotFound, ResultError.NotFound },
            { ErrorCategory.Conflict, ResultError.Conflict },
            { ErrorCategory.Unauthorized, ResultError.Unauthorized },
            { ErrorCategory.Forbidden, ResultError.Forbidden },
            { ErrorCategory.Unavailable, ResultError.Unavailable },
            { ErrorCategory.Internal, ResultError.Internal },
        };

    [Theory]
    [MemberData(nameof(Categories))]
    public void Factory_creates_error_with_stable_category_code_and_message(
        ErrorCategory category,
        Func<string, string, ResultError> factory)
    {
        ResultError error = factory("assets.invalid_state", "The asset state is invalid.");

        Assert.Equal(category, error.Category);
        Assert.Equal("assets.invalid_state", error.Code);
        Assert.Equal("The asset state is invalid.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Error_rejects_blank_codes(string code)
    {
        Assert.Throws<ArgumentException>(() => ResultError.Validation(code, "Invalid input."));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Error_rejects_blank_messages(string message)
    {
        Assert.Throws<ArgumentException>(() => ResultError.Validation("request.invalid", message));
    }
}
