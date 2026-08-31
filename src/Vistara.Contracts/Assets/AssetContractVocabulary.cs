using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Assets;

/// <summary>
/// The published vocabulary for the enum-like gallery asset fields. Storage and
/// the domain use enum names such as <c>Ready</c> and <c>Private</c>, while the
/// gallery contract documents lower-camel tokens such as <c>ready</c> and
/// <c>private</c>. Every response projection publishes through this vocabulary
/// so a single serialization boundary decides the casing.
/// </summary>
public static class AssetContractVocabulary
{
    /// <summary>The documented <c>AssetStatus</c> tokens.</summary>
    public static IReadOnlyList<string> Statuses { get; } =
        ["processing", "ready", "failed", "trashed", "purged"];

    /// <summary>The documented <c>AssetVisibility</c> tokens.</summary>
    public static IReadOnlyList<string> Visibilities { get; } =
        ["private", "tenant", "public"];

    /// <summary>
    /// The documented <c>AssetRendition.kind</c> tokens: the standard
    /// derivative preset names plus the original-source fallback.
    /// </summary>
    public static IReadOnlyList<string> RenditionKinds { get; } =
        ["thumb", "grid", "viewer", "download-web", "original"];

    /// <summary>
    /// The documented <c>AssetQueryStatus</c> tokens. The gallery list,
    /// timeline, and facet queries never return trashed or purged assets, so
    /// only the browsable statuses are filterable.
    /// </summary>
    public static IReadOnlyList<string> QueryStatuses { get; } =
        ["processing", "ready", "failed"];

    /// <summary>
    /// Translates a documented visibility token back to the stored enum name so
    /// mutations accept exactly the values the contract publishes.
    /// </summary>
    public static bool TryReadVisibility(string? token, out string storedValue) =>
        TryReadStored(token, Visibilities, out storedValue);

    /// <summary>
    /// Translates a documented query status token back to the stored enum name
    /// so filters accept exactly the values the contract publishes.
    /// </summary>
    public static bool TryReadQueryStatus(string? token, out string storedValue) =>
        TryReadStored(token, QueryStatuses, out storedValue);

    /// <summary>
    /// Publishes a stored status as the documented <c>AssetQueryStatus</c>
    /// token, which is the value a client feeds straight back into the
    /// <c>statuses</c> filter.
    /// </summary>
    public static string PublishQueryStatus(string? storedValue) =>
        Publish(storedValue, QueryStatuses);

    /// <summary>
    /// The human-readable label for a documented status token. The token is the
    /// machine value; this is only ever shown to a person.
    /// </summary>
    public static string DisplayQueryStatus(string? token)
    {
        string documented = Publish(token, QueryStatuses);
        return string.Concat(
            char.ToUpperInvariant(documented[0]).ToString(),
            documented[1..]);
    }

    private static bool TryReadStored(
        string? token,
        IReadOnlyList<string> documented,
        out string storedValue)
    {
        foreach (string candidate in documented)
        {
            if (string.Equals(candidate, token, StringComparison.Ordinal))
            {
                storedValue = string.Concat(
                    char.ToUpperInvariant(candidate[0]).ToString(),
                    candidate[1..]);
                return true;
            }
        }

        storedValue = string.Empty;
        return false;
    }

    /// <summary>
    /// Fails closed: a projection that carries a value outside the published
    /// vocabulary is a contract defect, and silently lower-casing it would hide
    /// exactly the drift this vocabulary exists to prevent.
    /// </summary>
    internal static string Publish(string? value, IReadOnlyList<string> documented)
    {
        foreach (string candidate in documented)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new JsonException(
            $"'{value}' is not a documented gallery token; " +
            $"expected one of {string.Join(", ", documented)}.");
    }
}

/// <summary>
/// Writes an enum-like gallery token using the documented casing regardless of
/// the representation the producing projection happened to carry.
/// </summary>
public abstract class AssetVocabularyJsonConverter : JsonConverter<string>
{
    private readonly IReadOnlyList<string> _documented;

    protected AssetVocabularyJsonConverter(IReadOnlyList<string> documented) =>
        _documented = documented;

    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        reader.GetString();

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(
            AssetContractVocabulary.Publish(value, _documented));
    }
}

public sealed class AssetStatusJsonConverter : AssetVocabularyJsonConverter
{
    public AssetStatusJsonConverter()
        : base(AssetContractVocabulary.Statuses)
    {
    }
}

public sealed class AssetVisibilityJsonConverter : AssetVocabularyJsonConverter
{
    public AssetVisibilityJsonConverter()
        : base(AssetContractVocabulary.Visibilities)
    {
    }
}

public sealed class AssetRenditionKindJsonConverter : AssetVocabularyJsonConverter
{
    public AssetRenditionKindJsonConverter()
        : base(AssetContractVocabulary.RenditionKinds)
    {
    }
}
