namespace Vistara.Contracts;

internal static class ContractGuards
{
    public static string RequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be empty or whitespace.", parameterName);
        }

        if (value.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value cannot exceed {maximumLength} characters.",
                parameterName);
        }

        return value;
    }

    public static string? OptionalText(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        return RequiredText(value, parameterName, maximumLength);
    }

    public static string UriReference(string value, string parameterName)
    {
        value = RequiredText(value, parameterName, 2_048);

        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out _))
        {
            throw new ArgumentException("The value must be a valid URI reference.", parameterName);
        }

        return value;
    }

    public static DateTimeOffset UtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value == default)
        {
            throw new ArgumentException("The timestamp must be specified.", parameterName);
        }

        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }
}
