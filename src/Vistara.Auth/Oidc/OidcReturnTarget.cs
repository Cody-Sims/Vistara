namespace Vistara.Auth.Oidc;

/// <summary>
/// Normalizes the post-sign-in return target supplied by a browser. The value
/// is attacker-controlled, so it is reduced to a path and query inside the
/// configured application origin; anything else falls back to the application
/// root instead of becoming an open redirect.
/// </summary>
public static class OidcReturnTarget
{
    public const int MaximumLength = 512;
    public const string Default = "/";

    public static bool TryCreate(
        string? candidate,
        Uri applicationBaseUri,
        out string returnTo)
    {
        ArgumentNullException.ThrowIfNull(applicationBaseUri);
        if (!applicationBaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The application base URL must be absolute.",
                nameof(applicationBaseUri));
        }

        returnTo = Default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return true;
        }

        if (candidate.Length > MaximumLength || !HasSafeCharacters(candidate))
        {
            return false;
        }

        if (!Uri.TryCreate(applicationBaseUri, candidate, out Uri? resolved) ||
            !resolved.IsAbsoluteUri)
        {
            return false;
        }

        if (!IsSameOrigin(resolved, applicationBaseUri) ||
            !string.IsNullOrEmpty(resolved.Fragment) ||
            !string.IsNullOrEmpty(resolved.UserInfo))
        {
            return false;
        }

        // A relative candidate must stay relative. `Uri.TryCreate` resolves
        // "//attacker.example" and "https://attacker.example" against the base
        // in ways that can silently change authority, and it also resolves a
        // bare "gallery" that a caller never intended as an application path.
        if (!IsExplicitAbsoluteCandidate(candidate) &&
            (candidate[0] != '/' || candidate.StartsWith("//", StringComparison.Ordinal)))
        {
            return false;
        }

        string path = resolved.AbsolutePath;
        string basePath = applicationBaseUri.AbsolutePath;
        if (!path.StartsWith(basePath, StringComparison.Ordinal) &&
            !string.Equals(path + "/", basePath, StringComparison.Ordinal))
        {
            return false;
        }

        string pathAndQuery = string.Concat(
            path.Length == 0 ? Default : path,
            resolved.Query);
        if (pathAndQuery.Length > MaximumLength ||
            pathAndQuery.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        returnTo = pathAndQuery;
        return true;
    }

    /// <summary>
    /// Rejects characters that let a candidate smuggle a second URL, a header,
    /// or a scheme past the origin check. Percent-encoded separators are also
    /// rejected because their decoded form changes the resolved authority.
    /// </summary>
    private static bool HasSafeCharacters(string candidate)
    {
        foreach (char character in candidate)
        {
            if (character is < ' ' or '\u007f' or '\\' ||
                char.IsWhiteSpace(character) ||
                character > '\u007e')
            {
                return false;
            }
        }

        return !ContainsEncodedSeparator(candidate) && !HasDotSegment(candidate);
    }

    private static bool ContainsEncodedSeparator(string candidate) =>
        candidate.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
        candidate.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
        candidate.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
        candidate.Contains("%00", StringComparison.OrdinalIgnoreCase);

    private static bool HasDotSegment(string candidate)
    {
        int queryStart = candidate.IndexOf('?', StringComparison.Ordinal);
        string path = queryStart < 0 ? candidate : candidate[..queryStart];
        return path
            .Split('/')
            .Any(segment => segment is "." or "..");
    }

    private static bool IsExplicitAbsoluteCandidate(string candidate) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out Uri? absolute) &&
        (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp);

    private static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.Ordinal) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}
