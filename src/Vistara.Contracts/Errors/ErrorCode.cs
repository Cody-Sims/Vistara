using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Errors;

/// <summary>
/// A stable, machine-readable error identifier using lower snake case.
/// </summary>
[JsonConverter(typeof(ErrorCodeJsonConverter))]
public readonly record struct ErrorCode
{
    public ErrorCode(string value)
    {
        value = ContractGuards.RequiredText(value, nameof(value), 128);

        if (value[0] is < 'a' or > 'z')
        {
            throw new ArgumentException("An error code must start with a lowercase letter.", nameof(value));
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '_')
            {
                throw new ArgumentException(
                    "An error code may contain only lowercase letters, digits, and underscores.",
                    nameof(value));
            }
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public sealed class ErrorCodeJsonConverter : JsonConverter<ErrorCode>
{
    public override ErrorCode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An error code must be a JSON string.");
        }

        try
        {
            return new ErrorCode(reader.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The error code is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ErrorCode value,
        JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("An error code must be specified.");
        }

        writer.WriteStringValue(value.Value);
    }
}
