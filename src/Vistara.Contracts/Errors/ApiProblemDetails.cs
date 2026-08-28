using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Vistara.Contracts.Errors;

/// <summary>
/// RFC 9457 Problem Details with Vistara's stable error and correlation extensions.
/// </summary>
public sealed class ApiProblemDetails
{
    [JsonConstructor]
    public ApiProblemDetails(
        string type,
        string title,
        int status,
        ErrorCode code,
        string? detail = null,
        string? instance = null,
        string? traceId = null,
        string? requestId = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        Type = ContractGuards.UriReference(type, nameof(type));
        Title = ContractGuards.RequiredText(title, nameof(title), 512);

        if (status is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "An HTTP status code must be between 100 and 599.");
        }

        if (code.IsEmpty)
        {
            throw new ArgumentException("An error code must be specified.", nameof(code));
        }

        Status = status;
        Code = code;
        Detail = ContractGuards.OptionalText(detail, nameof(detail), 4_096);
        Instance = instance is null
            ? null
            : ContractGuards.UriReference(instance, nameof(instance));
        TraceId = ContractGuards.OptionalText(traceId, nameof(traceId), 256);
        RequestId = ContractGuards.OptionalText(requestId, nameof(requestId), 256);
        Errors = CopyErrors(errors);
    }

    [JsonPropertyName("type")]
    public string Type { get; }

    [JsonPropertyName("title")]
    public string Title { get; }

    [JsonPropertyName("status")]
    public int Status { get; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; }

    [JsonPropertyName("instance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instance { get; }

    [JsonPropertyName("code")]
    public ErrorCode Code { get; }

    [JsonPropertyName("traceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TraceId { get; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; }

    [JsonPropertyName("errors")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Errors { get; }

    private static ReadOnlyDictionary<string, IReadOnlyList<string>> CopyErrors(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors)
    {
        var copy = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (errors is null)
        {
            return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
        }

        foreach (var (field, messages) in errors)
        {
            var validatedField = ContractGuards.RequiredText(field, nameof(errors), 512);
            ArgumentNullException.ThrowIfNull(messages);

            if (messages.Count == 0)
            {
                throw new ArgumentException(
                    "A validation error entry must contain at least one message.",
                    nameof(errors));
            }

            var messageCopy = new string[messages.Count];
            for (var index = 0; index < messages.Count; index++)
            {
                messageCopy[index] = ContractGuards.RequiredText(
                    messages[index],
                    nameof(errors),
                    2_048);
            }

            copy.Add(validatedField, Array.AsReadOnly(messageCopy));
        }

        return new ReadOnlyDictionary<string, IReadOnlyList<string>>(copy);
    }
}
