using System.Buffers;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vistara.Persistence.Model;

namespace Vistara.Persistence.Auth;

/// <summary>
/// A pending browser OIDC authorization request. Only digests of the
/// <c>state</c>, <c>nonce</c>, and browser handle values cross this boundary;
/// the raw values stay in the request pipeline and are never persisted.
/// </summary>
public sealed record OidcLoginRequest(
    byte[] StateDigest,
    string ProviderId,
    byte[] NonceDigest,
    byte[] HandleDigest,
    string CodeVerifier,
    string RedirectUri,
    string ReturnTo,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// The single successful read of a login request. Receiving this record proves
/// the caller won the atomic consume, so no other callback can complete the
/// same authorization.
/// </summary>
public sealed record ConsumedOidcLoginRequest(
    string ProviderId,
    byte[] NonceDigest,
    byte[] HandleDigest,
    string CodeVerifier,
    string RedirectUri,
    string ReturnTo,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset ConsumedAtUtc);

/// <summary>
/// Persists in-flight OIDC login requests for the hosted sign-in entry path.
/// </summary>
/// <remarks>
/// <para>
/// The store runs on <see cref="AuthenticationCatalogDbContext"/> because a
/// login request exists before any tenant scope does: the table carries no
/// <c>tenant_id</c> and is not row-level-security owned.
/// </para>
/// <para>
/// A request is single use. <see cref="ConsumeAsync"/> issues one conditional
/// <c>UPDATE ... WHERE consumed_at_utc IS NULL AND expires_at_utc &gt; @now</c>
/// and only reads the row back when that statement reports exactly one affected
/// row. PostgreSQL takes a row lock for the update and re-evaluates the
/// predicate against the committed row, and SQLite serializes writers, so
/// concurrent callbacks produce exactly one winner on both providers. Replay,
/// expiry, and an unknown state are deliberately indistinguishable: all three
/// return <see langword="null"/>.
/// </para>
/// </remarks>
public sealed class RelationalOidcLoginRequestStore(
    AuthenticationCatalogDbContext catalog)
{
    /// <summary>The RFC 7636 unreserved code-verifier alphabet.</summary>
    private static readonly SearchValues<char> CodeVerifierUnreserved =
        SearchValues.Create(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~");

    private readonly AuthenticationCatalogDbContext _catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>
    /// Stores a new login request. Returns <see langword="false"/> when the
    /// state digest already exists so an attacker-chosen collision can never
    /// overwrite a live request.
    /// </summary>
    public async ValueTask<bool> CreateAsync(
        OidcLoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireDigest(request.StateDigest, nameof(request.StateDigest));
        RequireDigest(request.NonceDigest, nameof(request.NonceDigest));
        RequireDigest(request.HandleDigest, nameof(request.HandleDigest));
        RequireProviderId(request.ProviderId);
        RequireCodeVerifier(request.CodeVerifier);
        RequireRedirectUri(request.RedirectUri);
        RequireReturnTo(request.ReturnTo);
        if (request.ExpiresAtUtc <= request.CreatedAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "A login request must expire after it was created.");
        }

        var row = new OidcLoginRequestRow
        {
            StateDigest = Copy(request.StateDigest),
            ProviderId = request.ProviderId,
            NonceDigest = Copy(request.NonceDigest),
            HandleDigest = Copy(request.HandleDigest),
            CodeVerifier = request.CodeVerifier,
            RedirectUri = request.RedirectUri,
            ReturnTo = request.ReturnTo,
            CreatedAtUtc = request.CreatedAtUtc,
            ExpiresAtUtc = request.ExpiresAtUtc,
            ConsumedAtUtc = null,
        };
        _catalog.OidcLoginRequests.Add(row);
        try
        {
            await _catalog.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
        finally
        {
            // The verifier must not linger in the change tracker, and a losing
            // insert must not keep the duplicate key attached to the context.
            _catalog.Entry(row).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Atomically marks the request identified by <paramref name="stateDigest"/>
    /// consumed and returns it. A replayed, expired, or unknown state yields
    /// <see langword="null"/> without disclosing which of the three it was. If
    /// the sweep removes the row between the claim and the read-back, the
    /// callback fails closed and the visitor simply signs in again.
    /// </summary>
    public async ValueTask<ConsumedOidcLoginRequest?> ConsumeAsync(
        byte[] stateDigest,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        RequireDigest(stateDigest, nameof(stateDigest));
        byte[] lookup = Copy(stateDigest);

        int claimed = await _catalog.OidcLoginRequests
            .Where(row =>
                row.StateDigest == lookup &&
                row.ConsumedAtUtc == null &&
                row.ExpiresAtUtc > nowUtc &&
                row.CreatedAtUtc <= nowUtc)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.ConsumedAtUtc, nowUtc),
                cancellationToken);
        if (claimed != 1)
        {
            return null;
        }

        OidcLoginRequestRow? row = await _catalog.OidcLoginRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.StateDigest == lookup,
                cancellationToken);
        if (row?.ConsumedAtUtc is not { } consumedAtUtc)
        {
            return null;
        }

        return new ConsumedOidcLoginRequest(
            row.ProviderId,
            Copy(row.NonceDigest),
            Copy(row.HandleDigest),
            row.CodeVerifier,
            row.RedirectUri,
            row.ReturnTo,
            row.CreatedAtUtc,
            row.ExpiresAtUtc,
            consumedAtUtc);
    }

    /// <summary>
    /// Deletes at most <paramref name="maximumRows"/> requests that expired at
    /// or before <paramref name="expiredBeforeUtc"/>. The bound keeps the
    /// opportunistic sweep on the sign-in path from turning into an unbounded
    /// delete.
    /// </summary>
    public async ValueTask<int> DeleteExpiredAsync(
        DateTimeOffset expiredBeforeUtc,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRows, 1);
        return await _catalog.OidcLoginRequests
            .Where(row => row.ExpiresAtUtc < expiredBeforeUtc)
            .OrderBy(row => row.ExpiresAtUtc)
            .Take(maximumRows)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Compares the digest of the browser handle cookie with the digest stored
    /// on the consumed row in fixed time, binding the callback to the browser
    /// that started the flow.
    /// </summary>
    public static bool HandleMatches(
        ConsumedOidcLoginRequest request,
        byte[] handleDigest)
    {
        ArgumentNullException.ThrowIfNull(request);
        return handleDigest is not null &&
            CryptographicOperations.FixedTimeEquals(
                request.HandleDigest,
                handleDigest);
    }

    private static byte[] Copy(byte[] value) => [.. value];

    private static void RequireDigest(byte[] value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length !=
            OidcLoginRequestPersistenceContributor.DigestLength)
        {
            throw new ArgumentException(
                "A stored digest must be a SHA-256 hash.",
                name);
        }
    }

    private static void RequireProviderId(string providerId)
    {
        ArgumentException.ThrowIfNullOrEmpty(providerId);
        if (providerId.Length >
            OidcLoginRequestPersistenceContributor.ProviderIdMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerId),
                "The provider identifier is longer than the stored column.");
        }

        foreach (char character in providerId)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_'))
            {
                throw new ArgumentException(
                    "The provider identifier must be a URL-safe token.",
                    nameof(providerId));
            }
        }
    }

    private static void RequireCodeVerifier(string codeVerifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(codeVerifier);
        if (codeVerifier.Length <
                OidcLoginRequestPersistenceContributor.CodeVerifierMinLength ||
            codeVerifier.Length >
                OidcLoginRequestPersistenceContributor.CodeVerifierMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(codeVerifier),
                "A PKCE code verifier is 43 to 128 characters long.");
        }

        if (codeVerifier.AsSpan().IndexOfAnyExcept(CodeVerifierUnreserved) >= 0)
        {
            throw new ArgumentException(
                "A PKCE code verifier only uses unreserved characters.",
                nameof(codeVerifier));
        }
    }

    private static void RequireRedirectUri(string redirectUri)
    {
        ArgumentException.ThrowIfNullOrEmpty(redirectUri);
        if (redirectUri.Length > OidcLoginRequestPersistenceContributor.UriMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(redirectUri),
                "The redirect URI is longer than the stored column.");
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "The redirect URI must be an absolute HTTP or HTTPS URI.",
                nameof(redirectUri));
        }
    }

    /// <summary>
    /// Rejects anything that is not a same-origin absolute path, matching the
    /// browser-side rules in <c>safeDestination.ts</c>. A crafted
    /// <c>returnTo</c> must never survive persistence and become an open
    /// redirect at the end of the callback.
    /// </summary>
    private static void RequireReturnTo(string returnTo)
    {
        ArgumentException.ThrowIfNullOrEmpty(returnTo);
        if (returnTo.Length > OidcLoginRequestPersistenceContributor.UriMaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(returnTo),
                "The return path is longer than the stored column.");
        }

        if (returnTo[0] != '/' ||
            returnTo.StartsWith("//", StringComparison.Ordinal) ||
            returnTo.Contains('\\', StringComparison.Ordinal) ||
            returnTo.AsSpan().IndexOfAny('\r', '\n') >= 0 ||
            returnTo.Contains('\t', StringComparison.Ordinal) ||
            !Uri.TryCreate(
                new Uri("https://vistara.invalid", UriKind.Absolute),
                returnTo,
                out Uri? resolved) ||
            resolved.Host != "vistara.invalid")
        {
            throw new ArgumentException(
                "The return path must be a same-origin absolute path.",
                nameof(returnTo));
        }
    }
}
