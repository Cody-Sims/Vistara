using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Cookies;

public sealed record LocalLoginRequest(
    string Login,
    string Password,
    TenantId? RequestedTenantId,
    string? ExistingSessionToken)
{
    public override string ToString() => "[LocalLoginRequest REDACTED]";
}

public sealed record LocalReauthenticationRequest(
    string Login,
    string Password,
    string SessionToken)
{
    public override string ToString() => "[LocalReauthenticationRequest REDACTED]";
}

public sealed record ExternalOidcLoginResult(
    string Issuer,
    string Subject,
    string? Email,
    string? DisplayName)
{
    public override string ToString() => "[ExternalOidcLoginResult REDACTED]";
}

public enum CookieAuthenticationStrength
{
    PrimaryCredential,
}

public sealed record CookieReauthenticationContext
{
    public CookieReauthenticationContext(
        UserId actorId,
        DateTimeOffset verifiedAtUtc,
        CookieAuthenticationStrength strength)
    {
        if (verifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Cookie reauthentication timestamps must use UTC.",
                nameof(verifiedAtUtc));
        }

        if (!Enum.IsDefined(strength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength));
        }

        ActorId = actorId;
        VerifiedAtUtc = verifiedAtUtc;
        Strength = strength;
    }

    public UserId ActorId { get; }

    public DateTimeOffset VerifiedAtUtc { get; }

    public CookieAuthenticationStrength Strength { get; }
}

public sealed record CookieAuthPrincipal(
    UserId UserId,
    TenantId? TenantId,
    TenantRole? Role,
    string AntiforgeryTokenDigest,
    CookieReauthenticationContext Reauthentication)
{
    public override string ToString() =>
        $"{nameof(CookieAuthPrincipal)} {{ UserId = {UserId}, TenantId = {TenantId}, Role = {Role}, AntiforgeryTokenDigest = [REDACTED], Reauthentication = [REDACTED] }}";
}

public sealed record IssuedBrowserSession(
    CookieAuthPrincipal Principal,
    BrowserCookie Cookie,
    string AntiforgeryToken)
{
    public override string ToString() =>
        $"{nameof(IssuedBrowserSession)} {{ Principal = {Principal}, Cookie = [REDACTED], AntiforgeryToken = [REDACTED] }}";
}

public sealed record AuthenticatedBrowserSession(
    CookieAuthPrincipal Principal,
    BrowserCookie? RefreshedCookie)
{
    public override string ToString() =>
        $"{nameof(AuthenticatedBrowserSession)} {{ Principal = {Principal}, RefreshedCookie = {(RefreshedCookie is null ? "null" : "[REDACTED]")} }}";
}

public interface ILocalCredentialVerifier
{
    ValueTask<User?> VerifyAsync(
        string login,
        string password,
        CancellationToken cancellationToken);
}

public interface IExternalIdentityLinker
{
    ValueTask<User?> ResolveOrLinkAsync(
        ExternalOidcLoginResult result,
        CancellationToken cancellationToken);
}

public interface ICookieTokenSource
{
    void Fill(Span<byte> destination);
}

public interface ICookieSessionStore
{
    ValueTask<CookieSessionRecord?> FindAsync(
        string sessionTokenDigest,
        CancellationToken cancellationToken);

    ValueTask<bool> AddAsync(
        CookieSessionRecord record,
        CancellationToken cancellationToken);

    ValueTask<bool> RotateAsync(
        string currentSessionTokenDigest,
        long expectedVersion,
        CookieSessionRecord replacement,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    ValueTask RevokeAsync(
        string sessionTokenDigest,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    ValueTask RevokeUserAsync(
        UserId userId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);

    ValueTask RevokeMembershipAsync(
        UserId userId,
        TenantId tenantId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);
}

public interface ICookieAuthAuditSink
{
    ValueTask WriteAsync(
        CookieAuthAuditEvent auditEvent,
        CancellationToken cancellationToken);
}

public enum CookieAuthAuditAction
{
    LoginSucceeded,
    LoginRejected,
    ReauthenticationSucceeded,
    ReauthenticationRejected,
    SessionAuthenticated,
    SessionRejected,
    SessionRotated,
    LoggedOut,
    SessionsInvalidated,
}

public sealed record CookieAuthAuditEvent(
    CookieAuthAuditAction Action,
    UserId? UserId,
    TenantId? TenantId,
    string? ReasonCode,
    DateTimeOffset OccurredAt)
{
    public override string ToString() =>
        $"{nameof(CookieAuthAuditEvent)} {{ Action = {Action}, UserId = {UserId}, TenantId = {TenantId}, ReasonCode = {ReasonCode}, OccurredAt = {OccurredAt:O} }}";
}
