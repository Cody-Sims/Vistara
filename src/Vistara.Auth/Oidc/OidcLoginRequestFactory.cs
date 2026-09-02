using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Oidc;

/// <summary>
/// Supplies the random material behind state, nonce, and PKCE verifiers. The
/// port exists so tests can pin the material without weakening the production
/// source.
/// </summary>
public interface IOidcRandomSource
{
    void Fill(Span<byte> destination);
}

public sealed class CryptographicOidcRandomSource : IOidcRandomSource
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

/// <summary>
/// One in-flight authorization request. The state and nonce digests are what a
/// server-side store persists; the state, nonce, and code verifier themselves
/// are secrets that must never be logged or returned to a browser body.
/// </summary>
public sealed record OidcLoginHandle
{
    internal OidcLoginHandle(
        string state,
        string nonce,
        string codeVerifier,
        string returnTo,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        State = state;
        Nonce = nonce;
        CodeVerifier = codeVerifier;
        CodeChallenge = OidcPkce.CreateChallenge(codeVerifier);
        StateDigest = OidcHandleCryptography.ComputeDigest(state);
        NonceDigest = OidcHandleCryptography.ComputeDigest(nonce);
        ReturnTo = returnTo;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string State { get; }

    public string StateDigest { get; }

    public string Nonce { get; }

    public string NonceDigest { get; }

    public string CodeVerifier { get; }

    public string CodeChallenge { get; }

    public string CodeChallengeMethod { get; } = OidcPkce.ChallengeMethod;

    public string ReturnTo { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public override string ToString() => "[OidcLoginHandle REDACTED]";
}

/// <summary>
/// Creates the cryptographic handles for one authorization-code sign-in and
/// normalizes the caller-supplied return target before it is stored.
/// </summary>
public sealed class OidcLoginRequestFactory
{
    private readonly OidcProviderOptions _options;
    private readonly IOidcRandomSource _randomSource;
    private readonly IClock _clock;

    public OidcLoginRequestFactory(
        OidcProviderOptions options,
        IOidcRandomSource randomSource,
        IClock clock)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public Result<OidcLoginHandle> Create(string? returnToCandidate)
    {
        if (!OidcReturnTarget.TryCreate(
                returnToCandidate,
                _options.ApplicationBaseUri,
                out string returnTo))
        {
            return Result.Failure<OidcLoginHandle>(OidcErrors.InvalidReturnTarget);
        }

        DateTimeOffset createdAt = _clock.UtcNow.ToUniversalTime();
        return Result.Success(
            new OidcLoginHandle(
                CreateHandle(),
                CreateHandle(),
                CreateHandle(),
                returnTo,
                createdAt,
                createdAt.Add(_options.LoginRequestLifetime)));
    }

    private string CreateHandle()
    {
        byte[] material = new byte[OidcBase64Url.HandleByteLength];
        try
        {
            _randomSource.Fill(material);
            return OidcBase64Url.Encode(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }
}
