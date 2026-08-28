using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Concurrency;

/// <summary>
/// The canonical strong ETag for a mutable resource: <c>"v{version}"</c>.
/// </summary>
[JsonConverter(typeof(EntityTagJsonConverter))]
public readonly record struct EntityTag
{
    public EntityTag(ResourceVersion version)
    {
        Version = version;
    }

    public ResourceVersion Version { get; }

    public static EntityTag Parse(string value)
    {
        value = ContractGuards.RequiredText(value, nameof(value), 32);

        if (value.Length < 4
            || value[0] != '"'
            || value[1] != 'v'
            || value[^1] != '"'
            || !long.TryParse(
                value.AsSpan(2, value.Length - 3),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version))
        {
            throw new FormatException("An ETag must use the canonical form \"v{version}\".");
        }

        var entityTag = new EntityTag(new ResourceVersion(version));
        if (!string.Equals(entityTag.ToString(), value, StringComparison.Ordinal))
        {
            throw new FormatException("An ETag must use the canonical form \"v{version}\".");
        }

        return entityTag;
    }

    public override string ToString() => $"\"v{Version.Value.ToString(CultureInfo.InvariantCulture)}\"";
}

public sealed class EntityTagJsonConverter : JsonConverter<EntityTag>
{
    public override EntityTag Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("An ETag must be a JSON string.");
        }

        try
        {
            return EntityTag.Parse(reader.GetString()!);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new JsonException("The ETag is invalid.", exception);
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        EntityTag value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
