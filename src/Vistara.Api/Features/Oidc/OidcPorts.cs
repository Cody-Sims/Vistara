using Vistara.Domain.Common;

namespace Vistara.Api.Features.Oidc;

/// <summary>
/// One provider a browser may start hosted sign-in with. Only the values a
/// first-run client needs to render a button are published: no directory
/// tenant, client identifier, authority, or allowlist ever leaves the server.
/// </summary>
public sealed record OidcProviderCapability(
    string ProviderId,
    string DisplayName,
    string StartPath);

/// <summary>
/// The browser-visible result of starting a sign-in: where to send the user
/// agent, and the opaque, protected value that binds the callback to this
/// browser. Neither the state, the nonce, nor the code verifier appears here.
/// </summary>
public sealed record OidcStartResult(Uri AuthorizationUri, string HandleCookieValue);

/// <summary>
/// A completed callback. The session cookie header is the existing Vistara
/// browser session; <see cref="ReturnTo"/> is an already-validated same-origin
/// application path.
/// </summary>
public sealed record OidcSignInResult(string SetCookieHeader, string ReturnTo);

/// <summary>
/// The result of a relying-party initiated sign-out. The Vistara session has
/// already been revoked when this is returned; <see cref="EndSessionUrl"/> is
/// where the browser may go to end the provider session as well, and is null
/// when the provider publishes no end-session endpoint or none was configured.
/// </summary>
public sealed record OidcSignOutResult(string SetCookieHeader, string? EndSessionUrl);

/// <summary>
/// The callback parameters exactly as the browser presented them. Every member
/// is untrusted input, so the port validates each one before it is used.
/// </summary>
public sealed record OidcCallbackCommand
{
    public OidcCallbackCommand(
        string providerId,
        string? state,
        string? code,
        string? error,
        string? handleCookieValue,
        string? existingSessionToken)
    {
        ProviderId = providerId;
        State = state;
        Code = code;
        Error = error;
        HandleCookieValue = handleCookieValue;
        ExistingSessionToken = existingSessionToken;
    }

    public string ProviderId { get; }

    public string? State { get; }

    public string? Code { get; }

    public string? Error { get; }

    public string? HandleCookieValue { get; }

    public string? ExistingSessionToken { get; }

    /// <summary>The code, state, and cookie are secrets; none may be printed.</summary>
    public override string ToString() =>
        $"{nameof(OidcCallbackCommand)} {{ ProviderId = {ProviderId} }}";
}

/// <summary>
/// Hosted OpenID Connect sign-in. Implementations own the whole server-side
/// flow: cryptographic handles, the single-use login request, the token
/// exchange, identity-token validation, the allowlists, and the handoff to the
/// existing Vistara cookie session.
/// </summary>
public interface IOidcLoginPort
{
    ValueTask<Result<OidcStartResult>> StartAsync(
        string providerId,
        string? returnTo,
        CancellationToken cancellationToken);

    ValueTask<Result<OidcSignInResult>> CompleteAsync(
        OidcCallbackCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// The hosted sign-in providers a first-run client may offer. The catalog is
/// always resolvable and simply reports nothing when hosted sign-in is not
/// configured, so the setup surface never has to ask whether the feature was
/// composed.
/// </summary>
public interface IOidcProviderCatalog
{
    IReadOnlyList<OidcProviderCapability> Providers { get; }
}

/// <summary>The catalog of a deployment with no hosted provider configured.</summary>
public sealed class EmptyOidcProviderCatalog : IOidcProviderCatalog
{
    public IReadOnlyList<OidcProviderCapability> Providers { get; } = [];
}

/// <summary>
/// Records the detailed outcome of a sign-in attempt server side. The browser
/// only ever receives one uniform error code, so this is the single place a
/// reviewer can tell a cancelled sign-in from a replayed state or a rejected
/// directory. Implementations must never receive or record an authorization
/// code, token, state, nonce, or code verifier.
/// </summary>
public interface IOidcAuditSink
{
    void Record(OidcAuditEvent auditEvent);
}

/// <summary>
/// One recorded sign-in outcome. <see cref="Detail"/> is a fixed vocabulary
/// value chosen by the adapter, never provider-supplied text.
/// </summary>
public sealed record OidcAuditEvent(
    string ProviderId,
    string Stage,
    string Detail,
    Guid? DirectoryTenantId = null,
    string? ObjectId = null,
    Guid? TenantId = null,
    Guid? UserId = null)
{
    /// <summary>
    /// The provider key as an operator will read it. A value that is not a
    /// provider key is replaced with a fixed token at construction, so a
    /// caller cannot put an attacker-chosen route segment into a log line by
    /// forgetting to check it first.
    /// </summary>
    public string ProviderId { get; init; } = OidcRoutes.ForAudit(ProviderId);
}
