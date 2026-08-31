using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Jwt;
using Vistara.Domain.Identity;

namespace Vistara.Api.Composition.Platform;

public enum PlatformAuthenticationKind
{
    Cookie,
    ApiKey,
    Bearer,
}

public enum PlatformAuthenticationStrength
{
    PrimaryCredential,
}

public sealed record PlatformReauthenticationContext
{
    public PlatformReauthenticationContext(
        Guid actorId,
        DateTimeOffset verifiedAtUtc,
        PlatformAuthenticationStrength strength)
    {
        if (actorId == Guid.Empty || actorId.Version != 7)
        {
            throw new ArgumentException(
                "The reauthenticated actor ID must be UUIDv7.",
                nameof(actorId));
        }

        if (verifiedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "The reauthentication timestamp must use UTC.",
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

    public Guid ActorId { get; }
    public DateTimeOffset VerifiedAtUtc { get; }
    public PlatformAuthenticationStrength Strength { get; }
}

public sealed record PlatformIdentity
{
    public PlatformIdentity(
        Guid userId,
        Guid tenantId,
        string role,
        IReadOnlyCollection<string> scopes,
        string? antiforgeryTokenDigest,
        PlatformReauthenticationContext? reauthentication = null)
    {
        if (userId == Guid.Empty || userId.Version != 7)
        {
            throw new ArgumentException("The user ID must be UUIDv7.", nameof(userId));
        }

        if (tenantId == Guid.Empty || tenantId.Version != 7)
        {
            throw new ArgumentException("The tenant ID must be UUIDv7.", nameof(tenantId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(scopes);
        if (reauthentication is not null &&
            reauthentication.ActorId != userId)
        {
            throw new ArgumentException(
                "Reauthentication must belong to the authenticated user.",
                nameof(reauthentication));
        }

        UserId = userId;
        TenantId = tenantId;
        Role = role;
        Scopes = scopes.ToArray();
        AntiforgeryTokenDigest = antiforgeryTokenDigest;
        Reauthentication = reauthentication;
    }

    public Guid UserId { get; }
    public Guid TenantId { get; }
    public string Role { get; }
    public IReadOnlyCollection<string> Scopes { get; }
    public string? AntiforgeryTokenDigest { get; }
    public PlatformReauthenticationContext? Reauthentication { get; }
}

public sealed record PlatformCredentialResult
{
    private PlatformCredentialResult(
        PlatformIdentity? identity,
        string? errorCode,
        string? refreshedCookie)
    {
        Identity = identity;
        ErrorCode = errorCode;
        RefreshedCookie = refreshedCookie;
    }

    public PlatformIdentity? Identity { get; }
    public string? ErrorCode { get; }
    public string? RefreshedCookie { get; }
    public bool IsSuccess => Identity is not null;

    public static PlatformCredentialResult Success(
        PlatformIdentity identity,
        string? refreshedCookie = null) =>
        new(identity ?? throw new ArgumentNullException(nameof(identity)), null, refreshedCookie);

    public static PlatformCredentialResult Invalid(string errorCode) =>
        new(null, NormalizeCode(errorCode), null);

    private static string NormalizeCode(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return errorCode;
    }
}

public interface IPlatformCookieAuthenticator
{
    ValueTask<PlatformCredentialResult> AuthenticateCookieAsync(
        string sessionToken,
        CancellationToken cancellationToken);
}

public interface IPlatformApiKeyAuthenticator
{
    ValueTask<PlatformCredentialResult> AuthenticateApiKeyAsync(
        string apiKey,
        HttpContext context,
        CancellationToken cancellationToken);
}

public interface IPlatformBearerAuthenticator
{
    ValueTask<PlatformCredentialResult> AuthenticateBearerAsync(
        string bearerToken,
        CancellationToken cancellationToken);
}

internal static class PlatformAuthenticationState
{
    internal static readonly object KindKey = new();
    internal static readonly object AntiforgeryDigestKey = new();
    internal static readonly object ReauthenticationKey = new();
    internal static readonly object FailureCodeKey = new();

    internal static AuthenticateResult ToAuthenticateResult(
        HttpContext context,
        string scheme,
        PlatformAuthenticationKind kind,
        PlatformCredentialResult result)
    {
        if (!result.IsSuccess)
        {
            context.Items[FailureCodeKey] =
                result.ErrorCode ?? "authentication.invalid_credentials";
            return AuthenticateResult.Fail(
                result.ErrorCode ?? "authentication.invalid_credentials");
        }

        PlatformIdentity identity = result.Identity!;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString("D")),
            new("tenant_id", identity.TenantId.ToString("D")),
            new(ClaimTypes.Role, identity.Role),
            new("vistara_auth_kind", kind.ToString()),
        };
        claims.AddRange(identity.Scopes.Select(scope => new Claim("scope", scope)));
        if (identity.Reauthentication is { } reauthentication)
        {
            claims.Add(new Claim(
                "auth_time",
                reauthentication.VerifiedAtUtc
                    .ToUnixTimeSeconds()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)));
            claims.Add(new Claim(
                "vistara_auth_strength",
                reauthentication.Strength.ToString()));
            context.Items[ReauthenticationKey] = reauthentication;
        }

        context.Items[KindKey] = kind;
        if (identity.AntiforgeryTokenDigest is not null)
        {
            context.Items[AntiforgeryDigestKey] = identity.AntiforgeryTokenDigest;
        }

        if (result.RefreshedCookie is not null)
        {
            context.Response.Headers.Append("Set-Cookie", result.RefreshedCookie);
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, scheme));
    }
}

internal abstract class PlatformAuthenticationHandlerBase(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(
        options,
        logger,
        encoder)
{
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        PlatformProblemWriter.WriteAsync(
            Context,
            StatusCodes.Status401Unauthorized,
            ResolveChallengeCode(),
            "Authentication is required",
            Context.RequestAborted);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        PlatformProblemWriter.WriteAsync(
            Context,
            StatusCodes.Status403Forbidden,
            "authorization.forbidden",
            "The request is forbidden",
            Context.RequestAborted);

    private string ResolveChallengeCode()
    {
        if (Scheme.Name == PlatformAuthenticationDefaults.ConfusedScheme)
        {
            return PlatformAuthenticationDefaults.SchemeConfusionCode;
        }

        if (Context.Items.TryGetValue(
                PlatformAuthenticationState.FailureCodeKey,
                out object? failureCode) &&
            failureCode is string code)
        {
            return code;
        }

        AuthenticateResult? result = Context.Features
            .Get<Microsoft.AspNetCore.Authentication.IAuthenticateResultFeature>()
            ?.AuthenticateResult;
        return result?.Failure?.Message switch
        {
            PlatformAuthenticationDefaults.SchemeConfusionCode =>
                PlatformAuthenticationDefaults.SchemeConfusionCode,
            { Length: > 0 } resultCode => resultCode,
            _ => "authentication.required",
        };
    }
}

internal sealed class PlatformCookieAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPlatformCookieAuthenticator authenticator) :
    PlatformAuthenticationHandlerBase(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = Request.Cookies[CookieAuthOptions.ProductionCookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            Context.Items[PlatformAuthenticationState.FailureCodeKey] =
                "cookie_auth.invalid_session";
            return AuthenticateResult.Fail("cookie_auth.invalid_session");
        }

        PlatformCredentialResult result = await authenticator.AuthenticateCookieAsync(
            token,
            Context.RequestAborted);
        return PlatformAuthenticationState.ToAuthenticateResult(
            Context,
            Scheme.Name,
            PlatformAuthenticationKind.Cookie,
            result);
    }
}

internal sealed class PlatformApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPlatformApiKeyAuthenticator authenticator) :
    PlatformAuthenticationHandlerBase(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Microsoft.Extensions.Primitives.StringValues values = Request.Headers[
            PlatformAuthenticationDefaults.ApiKeyHeaderName];
        string? value = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(value))
        {
            Context.Items[PlatformAuthenticationState.FailureCodeKey] =
                "api_keys.invalid_credentials";
            return AuthenticateResult.Fail("api_keys.invalid_credentials");
        }

        PlatformCredentialResult result = await authenticator.AuthenticateApiKeyAsync(
            value,
            Context,
            Context.RequestAborted);
        return PlatformAuthenticationState.ToAuthenticateResult(
            Context,
            Scheme.Name,
            PlatformAuthenticationKind.ApiKey,
            result);
    }
}

internal sealed class PlatformBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IPlatformBearerAuthenticator authenticator) :
    PlatformAuthenticationHandlerBase(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Microsoft.Extensions.Primitives.StringValues values =
            Request.Headers.Authorization;
        string? value = values.Count == 1 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(value[7..]))
        {
            Context.Items[PlatformAuthenticationState.FailureCodeKey] =
                "jwt.invalid_token";
            return AuthenticateResult.Fail("jwt.invalid_token");
        }

        PlatformCredentialResult result = await authenticator.AuthenticateBearerAsync(
            value[7..].Trim(),
            Context.RequestAborted);
        return PlatformAuthenticationState.ToAuthenticateResult(
            Context,
            Scheme.Name,
            PlatformAuthenticationKind.Bearer,
            result);
    }
}

internal sealed class PlatformConfusedAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) :
    PlatformAuthenticationHandlerBase(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Fail());

    private AuthenticateResult Fail()
    {
        Context.Items[PlatformAuthenticationState.FailureCodeKey] =
            PlatformAuthenticationDefaults.SchemeConfusionCode;
        return AuthenticateResult.Fail(
            PlatformAuthenticationDefaults.SchemeConfusionCode);
    }
}

internal sealed class PlatformAnonymousAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) :
    PlatformAuthenticationHandlerBase(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(AuthenticateResult.NoResult());
}

internal sealed class DefaultPlatformCookieAuthenticator(IServiceProvider services) :
    IPlatformCookieAuthenticator
{
    /// <summary>
    /// Authenticates a browser cookie against the tenant that owns the
    /// session. The owning tenant is resolved first from the tenant-independent
    /// routing table, because no tenant scope exists yet when authentication
    /// runs; the user, membership, and tenant are then validated inside a scope
    /// fixed to that tenant, so a cookie can neither read another tenant's rows
    /// nor move the request onto a tenant it does not belong to.
    /// </summary>
    public async ValueTask<PlatformCredentialResult> AuthenticateCookieAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        try
        {
            PlatformLoginSessionFactory? sessions =
                services.GetService<PlatformLoginSessionFactory>();
            if (sessions is null)
            {
                return PlatformCredentialResult.Invalid("authentication.unavailable");
            }

            if (!CookieTokenCryptography.TryComputeSessionDigest(
                    sessionToken,
                    out string digest))
            {
                return PlatformCredentialResult.Invalid("cookie_auth.invalid_session");
            }

            Guid? owner = await sessions.FindSessionTenantAsync(
                digest,
                cancellationToken);
            if (owner is not { } tenantId)
            {
                return PlatformCredentialResult.Invalid("cookie_auth.invalid_session");
            }

            await using TenantScopedSessions scoped = sessions.Create(tenantId);
            Vistara.Domain.Common.Result<AuthenticatedBrowserSession> result =
                await scoped.Sessions.AuthenticateAsync(sessionToken, cancellationToken);
            if (!result.TryGetValue(out AuthenticatedBrowserSession? session) ||
                session.Principal.TenantId is null ||
                session.Principal.Role is null ||
                session.Principal.TenantId.Value.Value != tenantId)
            {
                return PlatformCredentialResult.Invalid(
                    result.Error?.Code ?? "cookie_auth.invalid_session");
            }

            return PlatformCredentialResult.Success(
                new PlatformIdentity(
                    session.Principal.UserId.Value,
                    session.Principal.TenantId.Value.Value,
                    session.Principal.Role.Value.ToString(),
                    PlatformScopeMapper.ForRole(session.Principal.Role.Value),
                    session.Principal.AntiforgeryTokenDigest,
                    new PlatformReauthenticationContext(
                        session.Principal.Reauthentication.ActorId.Value,
                        session.Principal.Reauthentication.VerifiedAtUtc,
                        PlatformAuthenticationStrength.PrimaryCredential)),
                session.RefreshedCookie?.ToSetCookieHeader());
        }
        catch (InvalidOperationException)
        {
            return PlatformCredentialResult.Invalid("authentication.unavailable");
        }
    }
}

internal sealed class DefaultPlatformApiKeyAuthenticator(IServiceProvider services) :
    IPlatformApiKeyAuthenticator
{
    public async ValueTask<PlatformCredentialResult> AuthenticateApiKeyAsync(
        string apiKey,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ApiKeyScope requiredScope = PlatformScopeMapper.ForRequest(context.Request);
        try
        {
            ApiKeyAuthenticator? authenticator =
                services.GetService<ApiKeyAuthenticator>();
            if (authenticator is null)
            {
                return PlatformCredentialResult.Invalid("authentication.unavailable");
            }

            Vistara.Domain.Common.Result<ApiKeyPrincipal> result =
                await authenticator.AuthenticateAsync(
                    apiKey,
                    requiredScope,
                    cancellationToken);
            return result.TryGetValue(out ApiKeyPrincipal? principal)
                ? PlatformCredentialResult.Success(
                    new PlatformIdentity(
                        principal.OwnerId.Value,
                        principal.TenantId.Value,
                        "Member",
                        PlatformScopeMapper.ForApiKey(principal.Scopes),
                        null))
                : PlatformCredentialResult.Invalid(
                    result.Error?.Code ?? "api_keys.invalid_credentials");
        }
        catch (InvalidOperationException)
        {
            return PlatformCredentialResult.Invalid("authentication.unavailable");
        }
    }
}

internal sealed class DefaultPlatformBearerAuthenticator(IServiceProvider services) :
    IPlatformBearerAuthenticator
{
    public async ValueTask<PlatformCredentialResult> AuthenticateBearerAsync(
        string bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            JwtAuthenticator? authenticator = services.GetService<JwtAuthenticator>();
            if (authenticator is null)
            {
                return PlatformCredentialResult.Invalid("authentication.unavailable");
            }

            Vistara.Domain.Common.Result<JwtPrincipal> result =
                await authenticator.AuthenticateAsync(bearerToken, cancellationToken);
            return result.TryGetValue(out JwtPrincipal? principal)
                ? PlatformCredentialResult.Success(
                    new PlatformIdentity(
                        principal.UserId.Value,
                        principal.TenantId.Value,
                        principal.Role.ToString(),
                        PlatformScopeMapper.ForRole(principal.Role),
                        null))
                : PlatformCredentialResult.Invalid(
                    result.Error?.Code ?? "jwt.invalid_token");
        }
        catch (InvalidOperationException)
        {
            return PlatformCredentialResult.Invalid("authentication.unavailable");
        }
    }
}

internal static class PlatformScopeMapper
{
    internal static ApiKeyScope ForRequest(HttpRequest request) =>
        HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
            ? ApiKeyScope.ReadAssets
            : ApiKeyScope.UploadAssets;

    internal static IReadOnlyCollection<string> ForApiKey(ApiKeyScope scopes)
    {
        var result = new List<string>();
        Add(result, scopes, ApiKeyScope.ReadAssets, "assets.read");
        Add(result, scopes, ApiKeyScope.UploadAssets, "assets.upload");
        Add(result, scopes, ApiKeyScope.ManageMetadata, "metadata.manage");
        Add(result, scopes, ApiKeyScope.ManageApiKeys, "api_keys.manage");
        return result;
    }

    internal static IReadOnlyCollection<string> ForRole(
        Vistara.Domain.Tenancy.TenantRole role) =>
        role switch
        {
            Vistara.Domain.Tenancy.TenantRole.Viewer => ["assets.read"],
            Vistara.Domain.Tenancy.TenantRole.Member =>
                ["assets.read", "assets.upload", "metadata.manage", "shares.manage"],
            Vistara.Domain.Tenancy.TenantRole.TenantAdmin =>
                ["assets.read", "assets.upload", "metadata.manage", "shares.manage",
                    "members.manage", "api_keys.manage"],
            Vistara.Domain.Tenancy.TenantRole.TenantOwner =>
                ["assets.read", "assets.upload", "metadata.manage", "shares.manage",
                    "members.manage", "api_keys.manage", "quotas.manage"],
            _ => [],
        };

    private static void Add(
        List<string> values,
        ApiKeyScope configured,
        ApiKeyScope candidate,
        string value)
    {
        if ((configured & candidate) == candidate)
        {
            values.Add(value);
        }
    }
}
