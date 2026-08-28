using System.Text.Json.Serialization;

namespace Vistara.Contracts.Idempotency;

/// <summary>
/// Metadata retained to detect conflicting reuse of an idempotency key.
/// </summary>
public sealed class IdempotencyRequestMetadata
{
    [JsonConstructor]
    public IdempotencyRequestMetadata(
        IdempotencyKey idempotencyKey,
        string requestHash,
        DateTimeOffset expiresAt)
    {
        if (idempotencyKey.IsEmpty)
        {
            throw new ArgumentException(
                "An idempotency key must be specified.",
                nameof(idempotencyKey));
        }

        IdempotencyKey = idempotencyKey;
        RequestHash = ContractGuards.RequiredText(requestHash, nameof(requestHash), 512);
        ExpiresAt = ContractGuards.UtcTimestamp(expiresAt, nameof(expiresAt));
    }

    [JsonPropertyName("idempotencyKey")]
    public IdempotencyKey IdempotencyKey { get; }

    [JsonPropertyName("requestHash")]
    public string RequestHash { get; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; }
}
