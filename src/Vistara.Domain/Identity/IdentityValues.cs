using Vistara.Domain.Common;

namespace Vistara.Domain.Identity;

public readonly record struct NormalizedEmail
{
    private NormalizedEmail(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<NormalizedEmail> Create(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        int separatorIndex = normalized.IndexOf('@', StringComparison.Ordinal);

        if (normalized.Length is < 3 or > 320 ||
            separatorIndex <= 0 ||
            separatorIndex != normalized.LastIndexOf('@') ||
            separatorIndex == normalized.Length - 1 ||
            normalized.Any(char.IsWhiteSpace))
        {
            return Result.Failure<NormalizedEmail>(IdentityErrors.InvalidEmail);
        }

        return Result.Success(new NormalizedEmail(normalized));
    }

    public override string ToString() => Value;
}

public readonly record struct NormalizedLogin
{
    private NormalizedLogin(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<NormalizedLogin> Create(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is < 1 or > 320 || normalized.Any(char.IsWhiteSpace))
        {
            return Result.Failure<NormalizedLogin>(IdentityErrors.InvalidLocalLogin);
        }

        return Result.Success(new NormalizedLogin(normalized));
    }

    public override string ToString() => Value;
}

public readonly record struct ExternalIssuer
{
    private ExternalIssuer(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<ExternalIssuer> Create(string value)
    {
        string candidate = value?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return Result.Failure<ExternalIssuer>(IdentityErrors.InvalidExternalIssuer);
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
        };
        string normalized = builder.Uri.AbsoluteUri.TrimEnd('/');
        return Result.Success(new ExternalIssuer(normalized));
    }

    public override string ToString() => Value;
}

public readonly record struct SessionDigest
{
    public SessionDigest(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!IdentityValueValidation.IsSha256Hex(normalized))
        {
            throw new ArgumentException(
                IdentityErrors.InvalidSessionDigest.Message,
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ApiKeyPrefix
{
    private ApiKeyPrefix(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<ApiKeyPrefix> Create(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        bool validCharacters = normalized
            .Skip(4)
            .All(char.IsAsciiLetterOrDigit);

        if (normalized.Length is < 5 or > 128 ||
            !normalized.StartsWith("vst_", StringComparison.Ordinal) ||
            !validCharacters)
        {
            return Result.Failure<ApiKeyPrefix>(IdentityErrors.InvalidApiKeyPrefix);
        }

        return Result.Success(new ApiKeyPrefix(normalized));
    }

    public override string ToString() => Value;
}

public readonly record struct ApiKeyDigest
{
    private ApiKeyDigest(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<ApiKeyDigest> Create(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return IdentityValueValidation.IsSha256Hex(normalized)
            ? Result.Success(new ApiKeyDigest(normalized))
            : Result.Failure<ApiKeyDigest>(IdentityErrors.InvalidApiKeyDigest);
    }

    public override string ToString() => Value;
}

internal static class IdentityValueValidation
{
    public static bool IsSha256Hex(string value) =>
        value.Length == 64 &&
        value.All(character =>
            char.IsAsciiDigit(character) ||
            character is >= 'a' and <= 'f');
}
