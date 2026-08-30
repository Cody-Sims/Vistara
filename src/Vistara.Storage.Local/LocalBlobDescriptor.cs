namespace Vistara.Storage.Local;

internal sealed record LocalBlobDescriptor(
    string Key,
    long ContentLength,
    string ContentType,
    DateTimeOffset LastModifiedUtc,
    string Version,
    string EntityTag,
    string Sha256,
    Dictionary<string, string> Metadata);
