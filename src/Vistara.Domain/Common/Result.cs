namespace Vistara.Domain.Common;

public sealed class Result
{
    private Result(ResultError? error)
    {
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    public ResultError? Error { get; }

    public static Result Success() => new(null);

    public static Result Failure(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error);
    }

    public static Result<T> Success<T>(T value)
        where T : notnull =>
        Result<T>.CreateSuccess(value);

    public static Result<T> Failure<T>(ResultError error)
        where T : notnull =>
        Result<T>.CreateFailure(error);
}
