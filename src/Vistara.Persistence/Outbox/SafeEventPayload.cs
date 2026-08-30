using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vistara.Persistence.Outbox;

internal static class SafeEventPayload
{
    private const int MaximumPayloadBytes = 16 * 1024;
    private const string Redacted = "[redacted]";

    private static readonly string[] SensitiveNames =
    [
        "authorization",
        "token",
        "accesstoken",
        "refreshtoken",
        "apikey",
        "password",
        "secret",
        "credential",
        "cookie",
        "signedurl",
        "privatemetadata",
        "rawmetadata",
        "imagebody",
        "mediabody",
        "base64",
        "objectkey",
    ];

    internal static string Sanitize(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        if (System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            throw new ArgumentException(
                $"Event payloads cannot exceed {MaximumPayloadBytes} UTF-8 bytes.",
                nameof(payload));
        }

        JsonNode root;
        try
        {
            root = JsonNode.Parse(
                payload,
                documentOptions: new JsonDocumentOptions
                {
                    MaxDepth = 16,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                }) ?? throw new JsonException("The event payload cannot be null.");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Event payloads must be valid JSON.", nameof(payload), exception);
        }

        if (root is not JsonObject)
        {
            throw new ArgumentException(
                "Event payloads must be JSON metadata objects.",
                nameof(payload));
        }

        SanitizeNode(root);
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
        });
    }

    private static void SanitizeNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (string propertyName in obj.Select(item => item.Key).ToArray())
            {
                JsonNode? value = obj[propertyName];
                if (IsSensitiveName(propertyName) || ContainsPrivateMedia(value))
                {
                    obj[propertyName] = Redacted;
                }
                else if (value is not null)
                {
                    SanitizeNode(value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                JsonNode? value = array[index];
                if (ContainsPrivateMedia(value))
                {
                    array[index] = Redacted;
                }
                else if (value is not null)
                {
                    SanitizeNode(value);
                }
            }
        }
    }

    private static bool IsSensitiveName(string propertyName)
    {
        string normalized = new(
            propertyName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        return SensitiveNames.Any(normalized.Contains);
    }

    private static bool ContainsPrivateMedia(JsonNode? node)
    {
        if (node is not JsonValue value ||
            !value.TryGetValue(out string? text) ||
            string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("x-amz-credential=", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("x-amz-signature=", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("?signature=", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("?sig=", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("&sig=", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeEncodedMedia(text);
    }

    private static bool LooksLikeEncodedMedia(string value)
    {
        if (value.Length < 256 || value.Length % 4 != 0)
        {
            return false;
        }

        return value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '+' or '/' or '=');
    }
}
