using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Idempotency;

/// <summary>
/// An opaque client-provided key used to safely retry a request.
/// </summary>
[JsonConverter(typeof(IdempotencyKeyJsonConverter))]
public readonly record struct IdempotencyKey
{
    public IdempotencyKey(string value)
    {
        value = ContractGuards.RequiredText(value, nameof(value), 255);

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                throw new ArgumentException(
                    "An idempotency key cannot contain control or whitespace characters.",
                    nameof(value));
            }
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

public sealed class IdempotencyKeyJsonConverter : JsonConverter<IdempotencyKey>
{
    public override IdempotencyKey Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An idempotency key must be a JSON string.");
        }

        try
        {
            return new IdempotencyKey(reader.GetString()!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException("The idempotency key is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        IdempotencyKey value,
        JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("An idempotency key must be specified.");
        }

        writer.WriteStringValue(value.Value);
    }
}
