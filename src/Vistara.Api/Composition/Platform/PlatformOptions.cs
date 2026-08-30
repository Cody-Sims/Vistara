using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Delivery;
using Vistara.Auth.Jwt;

namespace Vistara.Api.Composition.Platform;

public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public PlatformAuthenticationOptions Authentication { get; set; } = new();
}

public sealed class PlatformAuthenticationOptions
{
    public PlatformApiKeyOptions ApiKeys { get; set; } = new();
    public PlatformJwtOptions Jwt { get; set; } = new();
}

public sealed class PlatformApiKeyOptions
{
    public string? CurrentPepperVersion { get; set; }
    public Dictionary<string, string> Peppers { get; set; } =
        new(StringComparer.Ordinal);
}

public sealed class PlatformJwtOptions
{
    public List<PlatformJwtIssuerOptions> Issuers { get; set; } = [];
}

public sealed class PlatformJwtIssuerOptions
{
    public string? ProfileId { get; set; }
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public string? MetadataAddress { get; set; }
    public List<string> AllowedAlgorithms { get; set; } = [];
    public List<string> AllowedTypes { get; set; } = [];
}

internal sealed class PlatformOptionsValidator : IValidateOptions<PlatformOptions>
{
    public ValidateOptionsResult Validate(string? name, PlatformOptions options)
    {
        try
        {
            _ = PlatformConfiguration.CreatePepperSet(options);
        }
        catch (Exception error) when (
            error is ArgumentException or FormatException)
        {
            return ValidateOptionsResult.Fail(
                "A valid API key pepper and current pepper version are required.");
        }

        try
        {
            _ = PlatformConfiguration.CreateIssuerProfiles(options);
        }
        catch (Exception error) when (
            error is ArgumentException or UriFormatException)
        {
            return ValidateOptionsResult.Fail(
                "At least one valid, explicitly configured JWT issuer is required.");
        }

        return ValidateOptionsResult.Success;
    }
}

internal static class PlatformConfiguration
{
    internal static ApiKeyPepperSet CreatePepperSet(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlatformApiKeyOptions configured = options.Authentication?.ApiKeys ??
            throw new ArgumentException("API key configuration is required.");
        var peppers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string version, string encodedSecret) in configured.Peppers)
        {
            peppers.Add(version, Convert.FromBase64String(encodedSecret));
        }

        return new ApiKeyPepperSet(configured.CurrentPepperVersion!, peppers);
    }

    internal static DeliveryGrantPepperSet CreateDeliveryPepperSet(
        PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        PlatformApiKeyOptions configured = options.Authentication?.ApiKeys ??
            throw new ArgumentException("API key configuration is required.");
        byte[] label = Encoding.UTF8.GetBytes(
            "vistara.delivery-grants.pepper.v1");
        try
        {
            var peppers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach ((string version, string encodedSecret) in configured.Peppers)
            {
                byte[] source = Convert.FromBase64String(encodedSecret);
                try
                {
                    peppers.Add(version, HMACSHA256.HashData(source, label));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(source);
                }
            }

            return new DeliveryGrantPepperSet(
                configured.CurrentPepperVersion!,
                peppers);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(label);
        }
    }

    internal static IReadOnlyCollection<JwtIssuerProfile> CreateIssuerProfiles(
        PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<PlatformJwtIssuerOptions> configured =
            options.Authentication?.Jwt?.Issuers ??
            throw new ArgumentException("JWT issuer configuration is required.");
        if (configured.Count == 0)
        {
            throw new ArgumentException("At least one JWT issuer is required.");
        }

        return configured.Select(issuer => JwtIssuerProfile.ForMetadata(
                issuer.ProfileId!,
                issuer.Issuer!,
                issuer.Audience!,
                new Uri(issuer.MetadataAddress!, UriKind.Absolute),
                issuer.AllowedAlgorithms,
                issuer.AllowedTypes.Count == 0 ? null : issuer.AllowedTypes))
            .ToArray();
    }
}
