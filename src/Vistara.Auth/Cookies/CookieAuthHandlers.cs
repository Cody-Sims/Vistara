using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Cookies;

public sealed class LocalLoginHandler
{
    private readonly ILocalCredentialVerifier _verifier;
    private readonly CookieSessionManager _sessions;

    public LocalLoginHandler(
        ILocalCredentialVerifier verifier,
        CookieSessionManager sessions)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async ValueTask<Result<IssuedBrowserSession>> HandleAsync(
        LocalLoginRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        User? user = await _verifier.VerifyAsync(
            request.Login,
            request.Password,
            cancellationToken);
        if (user?.Status != UserStatus.Active)
        {
            await _sessions.RecordLoginRejectionAsync(_sessions.UtcNow);
            return Result.Failure<IssuedBrowserSession>(
                CookieAuthErrors.InvalidCredentials);
        }

        Result<CookieSessionManager.TenantSelection> membershipResult =
            await _sessions.SelectMembershipAsync(
                user.Id,
                request.RequestedTenantId,
                cancellationToken);
        if (!membershipResult.TryGetValue(
                out CookieSessionManager.TenantSelection? selection))
        {
            return Result.Failure<IssuedBrowserSession>(
                membershipResult.Error!);
        }

        IssuedBrowserSession issued = await _sessions.IssueAsync(
            user,
            selection.Membership,
            request.ExistingSessionToken,
            cancellationToken);
        return Result.Success(issued);
    }
}

public sealed class ExternalOidcLoginHandler
{
    private readonly IExternalIdentityLinker _linker;
    private readonly CookieSessionManager _sessions;

    public ExternalOidcLoginHandler(
        IExternalIdentityLinker linker,
        CookieSessionManager sessions)
    {
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async ValueTask<Result<IssuedBrowserSession>> HandleAsync(
        ExternalOidcLoginResult externalResult,
        TenantId? requestedTenantId,
        string? existingSessionToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(externalResult);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(externalResult.Issuer) ||
            string.IsNullOrWhiteSpace(externalResult.Subject))
        {
            await _sessions.RecordLoginRejectionAsync(_sessions.UtcNow);
            return Result.Failure<IssuedBrowserSession>(
                CookieAuthErrors.InvalidCredentials);
        }

        User? user = await _linker.ResolveOrLinkAsync(
            externalResult,
            cancellationToken);
        if (user?.Status != UserStatus.Active)
        {
            await _sessions.RecordLoginRejectionAsync(_sessions.UtcNow);
            return Result.Failure<IssuedBrowserSession>(
                CookieAuthErrors.InvalidCredentials);
        }

        Result<CookieSessionManager.TenantSelection> membershipResult =
            await _sessions.SelectMembershipAsync(
                user.Id,
                requestedTenantId,
                cancellationToken);
        if (!membershipResult.TryGetValue(
                out CookieSessionManager.TenantSelection? selection))
        {
            return Result.Failure<IssuedBrowserSession>(
                membershipResult.Error!);
        }

        return Result.Success(
            await _sessions.IssueAsync(
                user,
                selection.Membership,
                existingSessionToken,
                cancellationToken));
    }
}

public sealed class LocalReauthenticationHandler
{
    private readonly ILocalCredentialVerifier _verifier;
    private readonly CookieSessionManager _sessions;

    public LocalReauthenticationHandler(
        ILocalCredentialVerifier verifier,
        CookieSessionManager sessions)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async ValueTask<Result<IssuedBrowserSession>> HandleAsync(
        LocalReauthenticationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        User? user = await _verifier.VerifyAsync(
            request.Login,
            request.Password,
            cancellationToken);
        if (user?.Status != UserStatus.Active)
        {
            await _sessions.RecordReauthenticationRejectionAsync(
                _sessions.UtcNow);
            return Result.Failure<IssuedBrowserSession>(
                CookieAuthErrors.InvalidCredentials);
        }

        return await _sessions.ReauthenticateAsync(
            user,
            request.SessionToken,
            cancellationToken);
    }
}

public sealed class CookieAuthenticationHandler(CookieSessionManager sessions)
{
    private readonly CookieSessionManager _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public ValueTask<Result<AuthenticatedBrowserSession>> AuthenticateAsync(
        string? sessionToken,
        CancellationToken cancellationToken) =>
        _sessions.AuthenticateAsync(sessionToken, cancellationToken);
}

public sealed class CookieLogoutHandler(CookieSessionManager sessions)
{
    private readonly CookieSessionManager _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public ValueTask<BrowserCookie> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken) =>
        _sessions.LogoutAsync(sessionToken, cancellationToken);
}

public sealed class CookieTenantSwitcher(CookieSessionManager sessions)
{
    private readonly CookieSessionManager _sessions =
        sessions ?? throw new ArgumentNullException(nameof(sessions));

    public ValueTask<Result<IssuedBrowserSession>> SwitchAsync(
        string? sessionToken,
        TenantId tenantId,
        CancellationToken cancellationToken) =>
        _sessions.SwitchTenantAsync(sessionToken, tenantId, cancellationToken);
}
