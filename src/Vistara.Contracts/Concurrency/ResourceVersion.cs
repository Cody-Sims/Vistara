using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Concurrency;

/// <summary>
/// An application-managed optimistic concurrency version.
/// </summary>
[JsonConverter(typeof(ResourceVersionJsonConverter))]
public readonly record struct ResourceVersion
{
    public ResourceVersion(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "A resource version cannot be negative.");
        }

        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class ResourceVersionJsonConverter : JsonConverter<ResourceVersion>
{
    public override ResourceVersion Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
        {
            throw new JsonException("A resource version must be a 64-bit JSON integer.");
        }

        try
        {
            return new ResourceVersion(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new JsonException("The resource version is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        ResourceVersion value,
        JsonSerializerOptions options) =>
        writer.WriteNumberValue(value.Value);
}
