using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Vistara.Api.Composition.Security;

public interface IVistaraDataProtectionRegistration;

internal sealed class VistaraDataProtectionRegistration :
    IVistaraDataProtectionRegistration;

public sealed class SecurityDataProtectionOptions
{
    public const string SectionName = "Security:DataProtection";

    public bool Enabled { get; set; }

    public string? ApplicationDiscriminator { get; set; }

    public string? BlobServiceUri { get; set; }

    public string? BlobContainerName { get; set; }

    public string? KeyBlobName { get; set; }

    public string? KeyVaultKeyIdentifier { get; set; }

    public string? ManagedIdentityClientId { get; set; }
}

/// <summary>
/// Seam for the Azure dependencies of the shared Data Protection key ring so
/// tests can substitute doubles without contacting Azure.
/// </summary>
public interface IDataProtectionRuntimeDependencies
{
    TokenCredential CreateManagedIdentityCredential(string clientId);

    void ConfigureKeyPersistence(IDataProtectionBuilder builder);

    void ConfigureKeyProtection(IDataProtectionBuilder builder);
}

/// <summary>
/// Holds the single process-wide managed identity credential used by both key
/// persistence and key protection.
/// </summary>
public sealed class DataProtectionCredentialSource
{
    private readonly Lazy<TokenCredential> credential;

    public DataProtectionCredentialSource(
        IDataProtectionRuntimeDependencies runtime,
        IOptions<SecurityDataProtectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        credential = new Lazy<TokenCredential>(
            () => runtime.CreateManagedIdentityCredential(
                options.Value.ManagedIdentityClientId!),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public TokenCredential Credential => credential.Value;
}

public static class DataProtectionServiceCollectionExtensions
{
    public static IServiceCollection AddVistaraApiDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment) =>
        AddVistaraApiDataProtection(services, configuration, environment, runtime: null);

    /// <summary>
    /// Registers the shared key ring with an explicit Azure dependency seam.
    /// Tests pass a double here; production passes <see langword="null" /> and
    /// gets the live Azure dependencies.
    /// </summary>
    public static IServiceCollection AddVistaraApiDataProtection(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        IDataProtectionRuntimeDependencies? runtime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IVistaraDataProtectionRegistration)))
        {
            return services;
        }

        services.AddSingleton<IVistaraDataProtectionRegistration,
            VistaraDataProtectionRegistration>();
        services.AddOptions<SecurityDataProtectionOptions>()
            .Bind(configuration.GetSection(
                SecurityDataProtectionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<SecurityDataProtectionOptions>>(
                new SecurityDataProtectionOptionsValidator(environment)));

        if (!IsExplicitlyEnabled(configuration))
        {
            // Disabled keeps the framework default key ring, which preserves
            // ephemeral and single-node local Compose behaviour.
            return services;
        }

        IDataProtectionRuntimeDependencies dependencies =
            ResolveDependencies(services, runtime);
        services.RemoveAll<IDataProtectionRuntimeDependencies>();
        services.AddSingleton(dependencies);
        services.TryAddSingleton<DataProtectionCredentialSource>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IPostConfigureOptions<DataProtectionOptions>,
                VistaraDataProtectionDiscriminatorOptions>());

        IDataProtectionBuilder builder = services.AddDataProtection();
        dependencies.ConfigureKeyPersistence(builder);
        dependencies.ConfigureKeyProtection(builder);
        return services;
    }

    private static bool IsExplicitlyEnabled(IConfiguration configuration) =>
        bool.TryParse(
            configuration[$"{SecurityDataProtectionOptions.SectionName}:Enabled"],
            out bool enabled) && enabled;

    /// <summary>
    /// Key persistence and protection are configured while the container is
    /// still being built, so a container-resolved dependency cannot be honoured.
    /// A pre-registration is therefore rejected instead of being silently
    /// replaced by the live Azure dependencies.
    /// </summary>
    private static IDataProtectionRuntimeDependencies ResolveDependencies(
        IServiceCollection services,
        IDataProtectionRuntimeDependencies? runtime)
    {
        if (runtime is not null)
        {
            return runtime;
        }

        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(IDataProtectionRuntimeDependencies)))
        {
            throw new InvalidOperationException(
                $"'{nameof(IDataProtectionRuntimeDependencies)}' is already "
                + "registered in the service collection. Pass it to "
                + $"'{nameof(AddVistaraApiDataProtection)}' explicitly; a "
                + "container registration cannot be honoured because the key "
                + "ring is configured before the container exists, and "
                + "ignoring it would silently fall back to live Azure "
                + "clients.");
        }

        return new DefaultDataProtectionRuntimeDependencies();
    }
}

internal sealed class VistaraDataProtectionDiscriminatorOptions(
    IOptions<SecurityDataProtectionOptions> options) :
    IPostConfigureOptions<DataProtectionOptions>
{
    public void PostConfigure(string? name, DataProtectionOptions target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.ApplicationDiscriminator =
            options.Value.ApplicationDiscriminator;
    }
}

internal sealed class DefaultDataProtectionRuntimeDependencies :
    IDataProtectionRuntimeDependencies
{
    public TokenCredential CreateManagedIdentityCredential(string clientId) =>
        new ManagedIdentityCredential(
            ManagedIdentityId.FromUserAssignedClientId(clientId));

    public void ConfigureKeyPersistence(IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.PersistKeysToAzureBlobStorage(static provider =>
        {
            SecurityDataProtectionOptions options = provider
                .GetRequiredService<IOptions<SecurityDataProtectionOptions>>()
                .Value;
            TokenCredential credential = provider
                .GetRequiredService<DataProtectionCredentialSource>()
                .Credential;
            var container = new BlobContainerClient(
                SecurityDataProtectionEndpoints.ContainerUri(options),
                credential);
            return container.GetBlobClient(options.KeyBlobName!);
        });
    }

    public void ConfigureKeyProtection(IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ProtectKeysWithAzureKeyVault(
            static provider => new Uri(
                provider
                    .GetRequiredService<
                        IOptions<SecurityDataProtectionOptions>>()
                    .Value
                    .KeyVaultKeyIdentifier!,
                UriKind.Absolute),
            static provider => provider
                .GetRequiredService<DataProtectionCredentialSource>()
                .Credential);
    }
}

internal static class SecurityDataProtectionEndpoints
{
    // Wave 8 provisions public Azure only. Sovereign clouds need a matching
    // BlobClientOptions.Audience and Key Vault authority, which this
    // composition does not configure, so they are rejected rather than
    // silently pointed at the public-cloud audience.
    internal const string PublicBlobHostSuffix = ".blob.core.windows.net";

    internal const string PublicVaultHostSuffix = ".vault.azure.net";

    internal static readonly string[] SovereignBlobHostSuffixes =
    [
        ".blob.core.usgovcloudapi.net",
        ".blob.core.chinacloudapi.cn",
        ".blob.core.cloudapi.de",
    ];

    internal static readonly string[] SovereignVaultHostSuffixes =
    [
        ".vault.usgovcloudapi.net",
        ".vault.azure.cn",
        ".vault.microsoftazure.de",
    ];

    internal static Uri ContainerUri(SecurityDataProtectionOptions options)
    {
        var serviceUri = new Uri(options.BlobServiceUri!, UriKind.Absolute);
        return new Uri(
            $"{serviceUri.GetLeftPart(UriPartial.Authority)}/{options.BlobContainerName}",
            UriKind.Absolute);
    }

    internal static bool IsTrustedEndpoint(
        string? value,
        string trustedHostSuffix,
        out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate) ||
            !string.Equals(
                candidate.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.Ordinal) ||
            !candidate.IsDefaultPort ||
            candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 ||
            candidate.Fragment.Length != 0 ||
            !candidate.Host.EndsWith(trustedHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    internal static bool IsSovereignEndpoint(
        string? value,
        IReadOnlyList<string> sovereignHostSuffixes) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? candidate) &&
        sovereignHostSuffixes.Any(suffix =>
            candidate.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}

internal sealed class SecurityDataProtectionOptionsValidator(
    IHostEnvironment environment) :
    IValidateOptions<SecurityDataProtectionOptions>
{
    private const string Section = SecurityDataProtectionOptions.SectionName;

    public ValidateOptionsResult Validate(
        string? name,
        SecurityDataProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        if (!options.Enabled)
        {
            if (IsAnyFieldConfigured(options) && !environment.IsDevelopment())
            {
                failures.Add(
                    $"'{Section}' has values but '{Section}:Enabled' is false; "
                    + "a hosted deployment must enable or remove the shared "
                    + "Data Protection key ring explicitly.");
            }

            return Result(failures);
        }

        ValidateDiscriminator(options.ApplicationDiscriminator, failures);
        ValidateManagedIdentity(options.ManagedIdentityClientId, failures);
        ValidatePairing(options, failures);
        ValidateBlobPersistence(options, failures);
        ValidateKeyVaultProtection(options.KeyVaultKeyIdentifier, failures);
        return Result(failures);
    }

    private static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

    private static bool IsAnyFieldConfigured(
        SecurityDataProtectionOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApplicationDiscriminator) ||
        !string.IsNullOrWhiteSpace(options.BlobServiceUri) ||
        !string.IsNullOrWhiteSpace(options.BlobContainerName) ||
        !string.IsNullOrWhiteSpace(options.KeyBlobName) ||
        !string.IsNullOrWhiteSpace(options.KeyVaultKeyIdentifier) ||
        !string.IsNullOrWhiteSpace(options.ManagedIdentityClientId);

    private static void ValidatePairing(
        SecurityDataProtectionOptions options,
        List<string> failures)
    {
        bool persistence =
            !string.IsNullOrWhiteSpace(options.BlobServiceUri) ||
            !string.IsNullOrWhiteSpace(options.BlobContainerName) ||
            !string.IsNullOrWhiteSpace(options.KeyBlobName);
        bool protection = !string.IsNullOrWhiteSpace(options.KeyVaultKeyIdentifier);
        if (persistence != protection)
        {
            failures.Add(
                $"'{Section}' requires Azure Blob key persistence and Azure Key "
                + "Vault key protection to be configured together; a hosted "
                + "deployment must never persist an unprotected key ring.");
        }
    }

    private static void ValidateDiscriminator(
        string? discriminator,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(discriminator) ||
            discriminator != discriminator.Trim() ||
            discriminator.Length > 128 ||
            discriminator.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '.' or '_' or ':')))
        {
            failures.Add(
                $"'{Section}:ApplicationDiscriminator' must be an explicit "
                + "value of up to 128 letters, digits, '-', '.', '_' or ':'; "
                + "replicas cannot share a key ring without it.");
        }
    }

    private static void ValidateManagedIdentity(
        string? clientId,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(clientId) ||
            !Guid.TryParseExact(clientId, "D", out Guid parsed) ||
            parsed == Guid.Empty)
        {
            failures.Add(
                $"'{Section}:ManagedIdentityClientId' must be the client id of "
                + "a user-assigned managed identity in 'xxxxxxxx-xxxx-xxxx-"
                + "xxxx-xxxxxxxxxxxx' form.");
        }
    }

    private static void ValidateBlobPersistence(
        SecurityDataProtectionOptions options,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.BlobServiceUri) &&
            string.IsNullOrWhiteSpace(options.BlobContainerName) &&
            string.IsNullOrWhiteSpace(options.KeyBlobName))
        {
            failures.Add(
                $"'{Section}:BlobServiceUri', '{Section}:BlobContainerName' and "
                + $"'{Section}:KeyBlobName' are required when the shared key "
                + "ring is enabled.");
            return;
        }

        if (!SecurityDataProtectionEndpoints.IsTrustedEndpoint(
                options.BlobServiceUri,
                SecurityDataProtectionEndpoints.PublicBlobHostSuffix,
                out Uri? serviceUri) ||
            !string.Equals(serviceUri!.AbsolutePath, "/", StringComparison.Ordinal))
        {
            failures.Add(
                SecurityDataProtectionEndpoints.IsSovereignEndpoint(
                    options.BlobServiceUri,
                    SecurityDataProtectionEndpoints.SovereignBlobHostSuffixes)
                    ? $"'{Section}:BlobServiceUri' must target the public Azure "
                        + "cloud; sovereign Azure clouds are unsupported here "
                        + "because the shared key ring configures no matching "
                        + "Blob service audience."
                    : $"'{Section}:BlobServiceUri' must be an HTTPS public Azure "
                        + "Blob service endpoint on the default port with no "
                        + "path, query or credentials.");
        }

        if (!IsValidContainerName(options.BlobContainerName))
        {
            failures.Add(
                $"'{Section}:BlobContainerName' must be a valid Azure Blob "
                + "container name of 3 to 63 lowercase letters, digits and "
                + "single hyphens.");
        }

        if (!IsValidBlobName(options.KeyBlobName))
        {
            failures.Add(
                $"'{Section}:KeyBlobName' must be a relative blob name without "
                + "traversal, backslashes, control characters or empty "
                + "segments.");
        }
    }

    private static void ValidateKeyVaultProtection(
        string? keyIdentifier,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(keyIdentifier))
        {
            failures.Add(
                $"'{Section}:KeyVaultKeyIdentifier' is required when the shared "
                + "key ring is enabled.");
            return;
        }

        if (!SecurityDataProtectionEndpoints.IsTrustedEndpoint(
                keyIdentifier,
                SecurityDataProtectionEndpoints.PublicVaultHostSuffix,
                out Uri? uri))
        {
            failures.Add(
                SecurityDataProtectionEndpoints.IsSovereignEndpoint(
                    keyIdentifier,
                    SecurityDataProtectionEndpoints.SovereignVaultHostSuffixes)
                    ? $"'{Section}:KeyVaultKeyIdentifier' must target the public "
                        + "Azure cloud; sovereign Azure clouds are unsupported "
                        + "here because the shared key ring configures no "
                        + "matching Key Vault authority."
                    : $"'{Section}:KeyVaultKeyIdentifier' must be an HTTPS public "
                        + "Azure Key Vault endpoint on the default port with no "
                        + "query or credentials.");
            return;
        }

        string[] segments = uri!.Segments;
        if (segments.Length != 3 ||
            !string.Equals(segments[1], "keys/", StringComparison.Ordinal) ||
            !IsValidKeyName(segments[2]))
        {
            failures.Add(
                $"'{Section}:KeyVaultKeyIdentifier' must be a versionless key "
                + "identifier of the form 'https://<vault>.vault.azure.net/"
                + "keys/<key>' so that key rotation does not require a "
                + "redeployment.");
        }
    }

    private static bool IsValidKeyName(string segment) =>
        segment.Length is > 0 and <= 127 &&
        segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidContainerName(string? containerName) =>
        containerName is { Length: >= 3 and <= 63 } &&
        IsContainerAlphanumeric(containerName[0]) &&
        IsContainerAlphanumeric(containerName[^1]) &&
        !containerName.Contains("--", StringComparison.Ordinal) &&
        containerName.All(character =>
            IsContainerAlphanumeric(character) || character == '-');

    private static bool IsContainerAlphanumeric(char character) =>
        char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character);

    private static bool IsValidBlobName(string? blobName)
    {
        if (string.IsNullOrWhiteSpace(blobName) ||
            blobName != blobName.Trim() ||
            blobName.Length > 1024 ||
            blobName.Contains('\\', StringComparison.Ordinal) ||
            blobName.Any(char.IsControl))
        {
            return false;
        }

        string[] segments = blobName.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            segment != "." &&
            segment != ".." &&
            segment == segment.Trim());
    }
}
