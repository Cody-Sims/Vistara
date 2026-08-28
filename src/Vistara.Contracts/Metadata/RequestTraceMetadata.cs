using System.Text.Json.Serialization;

namespace Vistara.Contracts.Metadata;

/// <summary>
/// Opaque request and distributed-trace identifiers returned for correlation.
/// </summary>
public sealed class RequestTraceMetadata
{
    [JsonConstructor]
    public RequestTraceMetadata(string requestId, string traceId)
    {
        RequestId = ContractGuards.RequiredText(requestId, nameof(requestId), 256);
        TraceId = ContractGuards.RequiredText(traceId, nameof(traceId), 256);
    }

    [JsonPropertyName("requestId")]
    public string RequestId { get; }

    [JsonPropertyName("traceId")]
    public string TraceId { get; }
}
