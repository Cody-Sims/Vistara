using System.Diagnostics.CodeAnalysis;

namespace Vistara.Domain.Common;

public sealed class Result<T>
    where T : notnull
{
    private readonly T? _value;

    private Result(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    private Result(ResultError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    public ResultError? Error { get; }

    internal static Result<T> CreateSuccess(T value) => new(value);

    internal static Result<T> CreateFailure(ResultError error) => new(error);

    public bool TryGetValue([NotNullWhen(true)] out T? value)
    {
        value = _value;
        return IsSuccess;
    }
}
