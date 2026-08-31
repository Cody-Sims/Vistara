using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// A signed JWT that proves client identity without a stored secret. In a
/// hosted Azure deployment the assertion comes from the container app's
/// managed identity through a federated identity credential, so Vistara never
/// holds an Entra application secret.
/// </summary>
public sealed record OidcClientAssertion
{
    public const string AssertionType =
        "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    public OidcClientAssertion(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => "[OidcClientAssertion REDACTED]";
}

/// <summary>
/// Supplies a managed-identity client assertion. Returning null means the
/// secretless path is not configured, not that authentication should be
/// skipped.
/// </summary>
public interface IOidcClientAssertionProvider
{
    ValueTask<OidcClientAssertion?> GetAssertionAsync(
        Uri tokenEndpoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Supplies an Entra application secret for deployments that cannot use a
/// federated managed identity. This is the explicit fallback, never the
/// default.
/// </summary>
public interface IOidcClientSecretProvider
{
    ValueTask<string?> GetSecretAsync(CancellationToken cancellationToken);
}

public enum OidcClientCredentialKind
{
    ClientAssertion,
    ClientSecret,
}

public sealed record OidcClientCredential(OidcClientCredentialKind Kind, string Value)
{
    public override string ToString() =>
        $"{nameof(OidcClientCredential)} {{ Kind = {Kind}, Value = [REDACTED] }}";
}

public interface IOidcClientCredentialProvider
{
    ValueTask<Result<OidcClientCredential>> GetAsync(
        Uri tokenEndpoint,
        CancellationToken cancellationToken);
}

/// <summary>
/// Prefers the secretless managed-identity assertion and falls back to a
/// configured application secret only when no assertion is available. A
/// faulting assertion provider degrades to the fallback rather than failing
/// the sign-in, but a deployment with neither credential fails closed instead
/// of attempting an anonymous token request.
/// </summary>
public sealed class OidcClientCredentialResolver : IOidcClientCredentialProvider
{
    private readonly IOidcClientAssertionProvider? _assertionProvider;
    private readonly IOidcClientSecretProvider? _secretProvider;

    public OidcClientCredentialResolver(
        IOidcClientAssertionProvider? assertionProvider,
        IOidcClientSecretProvider? secretProvider)
    {
        if (assertionProvider is null && secretProvider is null)
        {
            throw new ArgumentException(
                "A client assertion provider or a client secret provider is required.",
                nameof(assertionProvider));
        }

        _assertionProvider = assertionProvider;
        _secretProvider = secretProvider;
    }

    public async ValueTask<Result<OidcClientCredential>> GetAsync(
        Uri tokenEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        cancellationToken.ThrowIfCancellationRequested();
        if (_assertionProvider is not null)
        {
            OidcClientAssertion? assertion = null;
            try
            {
                assertion = await _assertionProvider
                    .GetAssertionAsync(tokenEndpoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception)
            {
                assertion = null;
            }
#pragma warning restore CA1031

            if (assertion is not null)
            {
                return Result.Success(
                    new OidcClientCredential(
                        OidcClientCredentialKind.ClientAssertion,
                        assertion.Value));
            }
        }

        if (_secretProvider is not null)
        {
            string? secret = null;
            try
            {
                secret = await _secretProvider
                    .GetSecretAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception)
            {
                secret = null;
            }
#pragma warning restore CA1031

            if (!string.IsNullOrWhiteSpace(secret))
            {
                return Result.Success(
                    new OidcClientCredential(OidcClientCredentialKind.ClientSecret, secret));
            }
        }

        return Result.Failure<OidcClientCredential>(OidcErrors.ClientCredentialUnavailable);
    }
}
