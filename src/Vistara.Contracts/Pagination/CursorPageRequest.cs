using System.Text.Json.Serialization;

namespace Vistara.Contracts.Pagination;

public sealed class CursorPageRequest
{
    public const int DefaultLimit = 60;
    public const int MaximumLimit = 200;

    [JsonConstructor]
    public CursorPageRequest(
        int limit = DefaultLimit,
        SignedCursor? cursor = null)
    {
        if (limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                $"The page limit must be between 1 and {MaximumLimit}.");
        }

        if (cursor is { IsEmpty: true })
        {
            throw new ArgumentException("A cursor cannot be empty.", nameof(cursor));
        }

        Limit = limit;
        Cursor = cursor;
    }

    [JsonPropertyName("limit")]
    public int Limit { get; }

    [JsonPropertyName("cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SignedCursor? Cursor { get; }
}
