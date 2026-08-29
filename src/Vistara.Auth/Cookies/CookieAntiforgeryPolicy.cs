using Vistara.Domain.Common;

namespace Vistara.Auth.Cookies;

public enum BrowserAuthenticationKind
{
    None,
    Cookie,
    Bearer,
    ApiKey,
}

public sealed class AntiforgeryDecision
{
    private AntiforgeryDecision(ResultError? error)
    {
        Error = error;
    }

    public bool IsAllowed => Error is null;

    public ResultError? Error { get; }

    public static AntiforgeryDecision Allow() => new(null);

    public static AntiforgeryDecision Reject(ResultError error) => new(error);
}

public sealed class CookieAntiforgeryPolicy
{
    private readonly HashSet<string> _safeMethods =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GET",
            "HEAD",
            "OPTIONS",
            "TRACE",
        };

    public AntiforgeryDecision Validate(
        string method,
        BrowserAuthenticationKind authenticationKind,
        string? presentedToken,
        string? expectedTokenDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (authenticationKind != BrowserAuthenticationKind.Cookie ||
            _safeMethods.Contains(method))
        {
            return AntiforgeryDecision.Allow();
        }

        return expectedTokenDigest is not null &&
            CookieTokenCryptography.FixedTimeMatches(
                presentedToken ?? string.Empty,
                expectedTokenDigest)
            ? AntiforgeryDecision.Allow()
            : AntiforgeryDecision.Reject(CookieAuthErrors.AntiforgeryRequired);
    }

    public AntiforgeryDecision Validate(
        string method,
        BrowserAuthenticationKind authenticationKind,
        IReadOnlyDictionary<string, string?> headers,
        string? expectedTokenDigest,
        CookieAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (authenticationKind != BrowserAuthenticationKind.Cookie ||
            _safeMethods.Contains(method))
        {
            return AntiforgeryDecision.Allow();
        }

        string? presentedToken = null;
        bool found = false;
        foreach ((string name, string? value) in headers)
        {
            if (!string.Equals(
                    name,
                    options.AntiforgeryHeaderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                return AntiforgeryDecision.Reject(
                    CookieAuthErrors.AntiforgeryRequired);
            }

            found = true;
            presentedToken = value;
        }

        return Validate(
            method,
            authenticationKind,
            presentedToken,
            expectedTokenDigest);
    }
}
