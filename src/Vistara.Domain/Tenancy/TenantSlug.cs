using Vistara.Domain.Common;

namespace Vistara.Domain.Tenancy;

public readonly record struct TenantSlug
{
    private TenantSlug(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Result<TenantSlug> Create(string value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length is < 1 or > 63 ||
            normalized[0] == '-' ||
            normalized[^1] == '-' ||
            normalized.Contains("--", StringComparison.Ordinal) ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            return Result.Failure<TenantSlug>(TenancyErrors.InvalidSlug);
        }

        return Result.Success(new TenantSlug(normalized));
    }

    public override string ToString() => Value;
}
