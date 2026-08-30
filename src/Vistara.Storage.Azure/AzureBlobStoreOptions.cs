using System.Net;
using Azure.Core;

namespace Vistara.Storage.Azure;

public enum AzureBlobCredentialMode
{
    TokenCredential,
    ConnectionString,
}

public enum AzureBlobSasMode
{
    UserDelegation,
    SharedKey,
}

public sealed class AzureBlobStoreOptions
{
    private static readonly string[] TrustedBlobHostSuffixes =
    [
        ".blob.core.windows.net",
        ".privatelink.blob.core.windows.net",
        ".blob.core.usgovcloudapi.net",
        ".privatelink.blob.core.usgovcloudapi.net",
        ".blob.core.chinacloudapi.cn",
        ".privatelink.blob.core.chinacloudapi.cn",
        ".blob.core.cloudapi.de",
        ".privatelink.blob.core.cloudapi.de",
        ".blob.storage.azure.net",
        ".privatelink.blob.storage.azure.net",
    ];

    public AzureBlobStoreOptions(
        string accountName,
        string containerName,
        Uri serviceUri,
        bool emulatorMode = false)
    {
        ValidateAccountName(accountName);
        ValidateContainerName(containerName);
        ArgumentNullException.ThrowIfNull(serviceUri);
        if (!serviceUri.IsAbsoluteUri ||
            !string.IsNullOrEmpty(serviceUri.UserInfo) ||
            serviceUri.Query.Length != 0 ||
            serviceUri.Fragment.Length != 0)
        {
            throw new ArgumentException(
                "The Azure Blob service endpoint must be an absolute URI without a query or fragment.",
                nameof(serviceUri));
        }

        if (!string.Equals(serviceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(emulatorMode &&
              string.Equals(serviceUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "The Azure Blob service endpoint must use HTTPS unless emulator mode is explicit.",
                nameof(serviceUri));
        }

        if (!emulatorMode && serviceUri.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "A production Azure Blob service endpoint cannot contain a path.",
                nameof(serviceUri));
        }

        if (emulatorMode)
        {
            if (!string.Equals(
                    serviceUri.AbsolutePath.Trim('/'),
                    accountName,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The emulator endpoint path must identify the configured Azure storage account.",
                    nameof(serviceUri));
            }
        }
        AccountName = accountName;
        ContainerName = containerName;
        ServiceUri = serviceUri;
        EmulatorMode = emulatorMode;
    }

    public string AccountName { get; }

    public string ContainerName { get; }

    public Uri ServiceUri { get; }

    public bool EmulatorMode { get; }

    public AzureBlobCredentialMode CredentialMode { get; init; } =
        AzureBlobCredentialMode.TokenCredential;

    public TokenCredential? TokenCredential { get; init; }

    public string? ConnectionString { get; init; }

    public IReadOnlyCollection<Uri> AllowedEndpointOrigins { get; init; } =
        Array.Empty<Uri>();

    public AzureBlobSasMode SasMode { get; init; } =
        AzureBlobSasMode.UserDelegation;

    public bool AllowSharedKeySas { get; init; }

    public TimeSpan MaximumGrantLifetime { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan CopyPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaximumCopyPollAttempts { get; init; } = 480;

    public int TransferBlockBytes { get; init; } = 4 * 1024 * 1024;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public override string ToString() =>
        $"{nameof(AzureBlobStoreOptions)} {{ AccountName = {AccountName}, ContainerName = {ContainerName}, ServiceUri = {ServiceUri}, EmulatorMode = {EmulatorMode}, CredentialMode = {CredentialMode}, ConnectionString = [redacted], SasMode = {SasMode} }}";

    internal void Validate()
    {
        if (!Enum.IsDefined(CredentialMode))
        {
            throw new ArgumentOutOfRangeException(nameof(CredentialMode));
        }

        if (!Enum.IsDefined(SasMode))
        {
            throw new ArgumentOutOfRangeException(nameof(SasMode));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            MaximumGrantLifetime,
            TimeSpan.Zero);
        if (MaximumGrantLifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumGrantLifetime),
                "Azure SAS grants cannot exceed seven days.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(
            CopyPollInterval,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            MaximumCopyPollAttempts,
            1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            TransferBlockBytes,
            1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            TransferBlockBytes,
            64 * 1024 * 1024);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        bool trustedAzureEndpoint = ValidateEndpointTrust();
        if (EmulatorMode &&
            CredentialMode != AzureBlobCredentialMode.ConnectionString)
        {
            throw new ArgumentException(
                "Azure Blob emulator mode requires explicit connection-string authentication.",
                nameof(CredentialMode));
        }

        if (CredentialMode == AzureBlobCredentialMode.TokenCredential)
        {
            if (!string.IsNullOrEmpty(ConnectionString))
            {
                throw new ArgumentException(
                    "A connection string is only accepted in explicit connection-string credential mode.",
                    nameof(ConnectionString));
            }

            if (SasMode == AzureBlobSasMode.SharedKey)
            {
                throw new ArgumentException(
                    "Shared-key SAS requires explicit connection-string credential mode.",
                    nameof(SasMode));
            }

            if (!trustedAzureEndpoint && TokenCredential is null)
            {
                throw new ArgumentException(
                    "An explicit token credential is required for an allowlisted non-Azure endpoint.",
                    nameof(TokenCredential));
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException(
                "Connection-string credential mode requires an injected connection string.",
                nameof(ConnectionString));
        }

        if (TokenCredential is not null)
        {
            throw new ArgumentException(
                "Connection-string credential mode cannot also specify a token credential.",
                nameof(TokenCredential));
        }

        if (SasMode != AzureBlobSasMode.SharedKey || !AllowSharedKeySas)
        {
            throw new ArgumentException(
                "Connection-string credentials require an explicit shared-key SAS opt-in.",
                nameof(AllowSharedKeySas));
        }
    }

    private bool ValidateEndpointTrust()
    {
        if (AllowedEndpointOrigins is null)
        {
            throw new ArgumentNullException(nameof(AllowedEndpointOrigins));
        }

        Uri serviceOrigin = NormalizeOrigin(
            ServiceUri,
            nameof(ServiceUri),
            allowHttp: EmulatorMode);
        HashSet<string> allowedOrigins = new(
            AllowedEndpointOrigins.Select(origin =>
                NormalizeOrigin(
                    origin,
                    nameof(AllowedEndpointOrigins),
                    allowHttp: false).AbsoluteUri),
            StringComparer.OrdinalIgnoreCase);
        bool trustedAzureEndpoint =
            ServiceUri.IsDefaultPort &&
            TrustedBlobHostSuffixes.Any(suffix =>
                string.Equals(
                    ServiceUri.Host,
                    $"{AccountName}{suffix}",
                    StringComparison.OrdinalIgnoreCase));
        if (EmulatorMode)
        {
            if (!IsLoopbackHost(ServiceUri))
            {
                throw new ArgumentException(
                    "Azure Blob emulator endpoints must use a loopback host.",
                    nameof(ServiceUri));
            }

            return false;
        }

        if (!EmulatorMode &&
            !trustedAzureEndpoint &&
            !allowedOrigins.Contains(serviceOrigin.AbsoluteUri))
        {
            throw new ArgumentException(
                "The Azure Blob endpoint must use a trusted Azure cloud or private-link origin, or be explicitly allowlisted.",
                nameof(ServiceUri));
        }

        return trustedAzureEndpoint;
    }

    private static bool IsLoopbackHost(Uri value)
    {
        if (string.Equals(
                value.DnsSafeHost,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(value.DnsSafeHost, out IPAddress? address) &&
            IPAddress.IsLoopback(address);
    }

    private static Uri NormalizeOrigin(
        Uri value,
        string parameterName,
        bool allowHttp)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri ||
            (!string.Equals(
                 value.Scheme,
                 Uri.UriSchemeHttps,
                 StringComparison.OrdinalIgnoreCase) &&
             !(allowHttp &&
               string.Equals(
                   value.Scheme,
                   Uri.UriSchemeHttp,
                   StringComparison.OrdinalIgnoreCase))) ||
            !string.IsNullOrEmpty(value.UserInfo) ||
            (!allowHttp && value.AbsolutePath != "/") ||
            value.Query.Length != 0 ||
            value.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(value.Host))
        {
            throw new ArgumentException(
                "Allowed Azure Blob endpoints must be HTTPS origins without credentials, paths, queries, or fragments.",
                parameterName);
        }

        return new Uri(value.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
    }

    private static void ValidateAccountName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 3 or > 24 ||
            value.Any(character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character)))
        {
            throw new ArgumentException(
                "Azure storage account names must be 3-24 lowercase letters or digits.",
                nameof(value));
        }
    }

    private static void ValidateContainerName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length is < 3 or > 63 ||
            value[0] == '-' ||
            value[^1] == '-' ||
            value.Contains("--", StringComparison.Ordinal) ||
            value.Any(character =>
                !char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character != '-'))
        {
            throw new ArgumentException(
                "Azure Blob container names must be 3-63 lowercase letters, digits, or single hyphens.",
                nameof(value));
        }
    }
}
