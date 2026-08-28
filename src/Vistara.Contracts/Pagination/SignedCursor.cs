using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Pagination;

/// <summary>
/// An opaque, signed keyset cursor. Its payload and signature are server concerns.
/// </summary>
[JsonConverter(typeof(SignedCursorJsonConverter))]
public readonly record struct SignedCursor
{
    public SignedCursor(string value)
    {
        value = ContractGuards.RequiredText(value, nameof(value), 4_096);

        foreach (var character in value)
        {
            if (character is < '!' or > '~')
            {
                throw new ArgumentException(
                    "A signed cursor must contain only visible ASCII characters.",
                    nameof(value));
            }
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public sealed class SignedCursorJsonConverter : JsonConverter<SignedCursor>
{
    public override SignedCursor Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A signed cursor must be a JSON string.");
        }

        try
        {
            return new SignedCursor(reader.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The signed cursor is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        SignedCursor value,
        JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("A signed cursor must be specified.");
        }

        writer.WriteStringValue(value.Value);
    }
}
