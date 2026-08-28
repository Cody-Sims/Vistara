using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Pagination;

/// <summary>
/// A keyset page. Exact total counts are intentionally not part of the contract.
/// </summary>
public sealed class CursorPage<T>
{
    [JsonConstructor]
    public CursorPage(
        IReadOnlyList<T> items,
        SignedCursor? nextCursor = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (nextCursor is { IsEmpty: true })
        {
            throw new ArgumentException("A next cursor cannot be empty.", nameof(nextCursor));
        }

        Items = new ReadOnlyCollection<T>(items.ToArray());
        NextCursor = nextCursor;
    }

    [JsonPropertyName("items")]
    public IReadOnlyList<T> Items { get; }

    [JsonPropertyName("nextCursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SignedCursor? NextCursor { get; }
}
