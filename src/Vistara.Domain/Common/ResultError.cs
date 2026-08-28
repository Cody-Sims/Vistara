namespace Vistara.Domain.Common;

public sealed record ResultError
{
    private ResultError(ErrorCategory category, string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Category = category;
        Code = code;
        Message = message;
    }

    public ErrorCategory Category { get; }

    public string Code { get; }

    public string Message { get; }

    public static ResultError Validation(string code, string message) =>
        new(ErrorCategory.Validation, code, message);

    public static ResultError NotFound(string code, string message) =>
        new(ErrorCategory.NotFound, code, message);

    public static ResultError Conflict(string code, string message) =>
        new(ErrorCategory.Conflict, code, message);

    public static ResultError Unauthorized(string code, string message) =>
        new(ErrorCategory.Unauthorized, code, message);

    public static ResultError Forbidden(string code, string message) =>
        new(ErrorCategory.Forbidden, code, message);

    public static ResultError Unavailable(string code, string message) =>
        new(ErrorCategory.Unavailable, code, message);

    public static ResultError Internal(string code, string message) =>
        new(ErrorCategory.Internal, code, message);
}
