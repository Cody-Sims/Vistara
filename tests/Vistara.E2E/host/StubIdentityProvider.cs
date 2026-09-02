using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Vistara.Auth.Oidc;

// The E2E host is a top-level program in the global namespace. A namespace of
// its own here would be named Vistara.E2E.Host, which the sibling test project
// would then resolve 'Host' to instead of the hosting API.

/// <summary>
/// Everything the stub identity provider needs to stand in for one Entra
/// directory: where it listens, which application it issues tokens to, and the
/// reply URLs it will hand a browser back to.
/// </summary>
internal sealed record StubIdentityProviderOptions(
    int Port,
    Guid DirectoryTenantId,
    string ClientId,
    string ClientSecret,
    Uri RedirectUri,
    Uri PostLogoutRedirectUri,
    string CertificatePath);

/// <summary>
/// A real OpenID Connect provider on the HTTPS loopback interface, for the
/// end-to-end suite.
///
/// It is a live TLS server with a real browser surface rather than a stubbed
/// message handler, because the flow under test is a browser flow: the visitor
/// leaves Vistara for another origin, consents there, and is redirected back.
/// Discovery, the key set, and the token exchange travel over the API's own
/// redirect-disabled client, and the identity token is genuinely signed with a
/// key published in the key set, so the API validates it exactly as it would a
/// directory-issued one.
///
/// Nothing here is production code and nothing here is reachable from one: the
/// certificate is self-signed, written to the artifacts folder, and trusted by
/// exactly one API process for exactly one run.
/// </summary>
internal static class StubIdentityProvider
{
    private const string SigningKeyId = "e2e-stub-signing-key";

    internal static async Task RunAsync(StubIdentityProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using X509Certificate2 certificate = LoopbackTls.CreateCertificate();
        // The API pins this certificate rather than trusting the machine, so
        // the public part — and only the public part — is published where the
        // run can find it.
        await LoopbackTls.PublishAsync(certificate, options.CertificatePath);

        using RSA signingKey = RSA.Create(2048);
        var authorizations = new ConcurrentDictionary<string, PendingAuthorization>(
            StringComparer.Ordinal);
        var grants = new ConcurrentDictionary<string, DirectoryGrant>(
            StringComparer.Ordinal);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(
                IPAddress.Loopback,
                options.Port,
                listen => listen.UseHttps(certificate)));
        WebApplication app = builder.Build();

        string tenant = options.DirectoryTenantId.ToString("D");
        string origin = string.Create(
            CultureInfo.InvariantCulture,
            $"https://127.0.0.1:{options.Port}");
        string issuer = $"{origin}/{tenant}/v2.0";

        app.MapGet("/health", () => Results.Text("ready"));

        app.MapGet(
            $"/{tenant}/v2.0/.well-known/openid-configuration",
            () => Results.Text(
                Metadata(issuer, origin, tenant),
                "application/json"));

        app.MapGet(
            $"/{tenant}/discovery/v2.0/keys",
            () => Results.Text(KeySet(signingKey), "application/json"));

        app.MapGet(
            $"/{tenant}/oauth2/v2.0/authorize",
            (HttpContext context) => Authorize(context, options, authorizations, tenant));

        app.MapPost(
            $"/{tenant}/oauth2/v2.0/authorize",
            (Delegate)((HttpContext context) =>
                ConsentAsync(context, authorizations, grants)));

        app.MapPost(
            $"/{tenant}/oauth2/v2.0/token",
            (Delegate)((HttpContext context) => IssueTokenAsync(
                context,
                options,
                grants,
                signingKey,
                issuer)));

        app.MapGet(
            $"/{tenant}/oauth2/v2.0/logout",
            (HttpContext context) => EndSession(context, options));

        await app.RunAsync();
    }

    /// <summary>
    /// The authorization request. Everything the API is required to send is
    /// checked here, so a flow that quietly dropped PKCE, the nonce, or the
    /// registered reply URL would fail the suite rather than pass it.
    /// </summary>
    private static IResult Authorize(
        HttpContext context,
        StubIdentityProviderOptions options,
        ConcurrentDictionary<string, PendingAuthorization> authorizations,
        string tenant)
    {
        IQueryCollection query = context.Request.Query;
        string? redirectUri = query["redirect_uri"];
        if (!string.Equals(
                redirectUri,
                options.RedirectUri.AbsoluteUri,
                StringComparison.Ordinal))
        {
            return Results.BadRequest("unregistered redirect_uri");
        }

        if (!string.Equals(query["client_id"], options.ClientId, StringComparison.Ordinal) ||
            !string.Equals(query["response_type"], "code", StringComparison.Ordinal) ||
            !string.Equals(
                query["code_challenge_method"],
                OidcPkce.ChallengeMethod,
                StringComparison.Ordinal) ||
            string.IsNullOrEmpty(query["code_challenge"]) ||
            string.IsNullOrEmpty(query["state"]) ||
            string.IsNullOrEmpty(query["nonce"]))
        {
            return Results.BadRequest("incomplete authorization request");
        }

        string requestId = Guid.NewGuid().ToString("N");
        authorizations[requestId] = new PendingAuthorization(
            query["state"]!,
            query["nonce"]!,
            query["code_challenge"]!,
            redirectUri!);
        return Results.Content(
            ConsentPage(requestId, tenant, options.DirectoryTenantId),
            "text/html; charset=utf-8");
    }

    /// <summary>
    /// The visitor's decision. Consent issues a code bound to this request's
    /// nonce and PKCE challenge; refusal returns the same error a real provider
    /// does, which the API must translate into its one browser-visible code.
    /// </summary>
    private static async Task<IResult> ConsentAsync(
        HttpContext context,
        ConcurrentDictionary<string, PendingAuthorization> authorizations,
        ConcurrentDictionary<string, DirectoryGrant> grants)
    {
        IFormCollection form = await context.Request.ReadFormAsync(
            context.RequestAborted);
        if (!authorizations.TryRemove(
                form["request"].ToString(),
                out PendingAuthorization? pending))
        {
            return Results.BadRequest("unknown authorization request");
        }

        if (!string.Equals(form["decision"], "allow", StringComparison.Ordinal))
        {
            return Results.Redirect(
                $"{pending.RedirectUri}?error=access_denied&state={Uri.EscapeDataString(pending.State)}");
        }

        if (!Guid.TryParseExact(form["oid"].ToString().Trim(), "D", out Guid objectId) ||
            !Guid.TryParseExact(form["tid"].ToString().Trim(), "D", out Guid directoryTenantId))
        {
            return Results.BadRequest("an identity needs an object and tenant identifier");
        }

        string code = $"code-{Guid.NewGuid():N}";
        grants[code] = new DirectoryGrant(
            pending.Nonce,
            pending.CodeChallenge,
            pending.RedirectUri,
            objectId,
            directoryTenantId,
            form["email"].ToString().Trim(),
            form["name"].ToString().Trim());
        return Results.Redirect(
            $"{pending.RedirectUri}?code={Uri.EscapeDataString(code)}"
            + $"&state={Uri.EscapeDataString(pending.State)}");
    }

    /// <summary>
    /// The back-channel exchange. The code is single use, the client credential
    /// is required in the body, and the verifier must match the challenge that
    /// arrived with the authorization request.
    /// </summary>
    private static async Task<IResult> IssueTokenAsync(
        HttpContext context,
        StubIdentityProviderOptions options,
        ConcurrentDictionary<string, DirectoryGrant> grants,
        RSA signingKey,
        string issuer)
    {
        IFormCollection form = await context.Request.ReadFormAsync(
            context.RequestAborted);
        if (!grants.TryRemove(form["code"].ToString(), out DirectoryGrant? grant) ||
            !string.Equals(form["client_id"], options.ClientId, StringComparison.Ordinal) ||
            !string.Equals(form["client_secret"], options.ClientSecret, StringComparison.Ordinal) ||
            !string.Equals(form["redirect_uri"], grant.RedirectUri, StringComparison.Ordinal) ||
            !string.Equals(
                OidcPkce.CreateChallenge(form["code_verifier"].ToString()),
                grant.CodeChallenge,
                StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "invalid_grant" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        string idToken = CreateIdToken(grant, options, signingKey, issuer);
        return Results.Json(new
        {
            token_type = "Bearer",
            expires_in = 3600,
            id_token = idToken,
        });
    }

    /// <summary>
    /// Relying-party initiated sign-out. Only the reply URL the deployment
    /// registered is honoured, which is what makes the redirect at the end of
    /// sign-out a provider decision rather than a caller-chosen one.
    /// </summary>
    private static IResult EndSession(
        HttpContext context,
        StubIdentityProviderOptions options)
    {
        string? postLogout = context.Request.Query["post_logout_redirect_uri"];
        return string.Equals(
                postLogout,
                options.PostLogoutRedirectUri.AbsoluteUri,
                StringComparison.Ordinal)
            ? Results.Redirect(postLogout!)
            : Results.BadRequest("unregistered post_logout_redirect_uri");
    }

    private static string CreateIdToken(
        DirectoryGrant grant,
        StubIdentityProviderOptions options,
        RSA signingKey,
        string issuer)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string payload = $$"""
            {
              "iss": "{{issuer}}",
              "aud": "{{options.ClientId}}",
              "sub": "subject-{{grant.ObjectId:N}}",
              "oid": "{{grant.ObjectId:D}}",
              "tid": "{{grant.DirectoryTenantId:D}}",
              "ver": "2.0",
              "nonce": "{{grant.Nonce}}",
              "preferred_username": "{{grant.Email}}",
              "name": "{{grant.DisplayName}}",
              "iat": {{now.ToUnixTimeSeconds()}},
              "nbf": {{now.AddMinutes(-1).ToUnixTimeSeconds()}},
              "exp": {{now.AddMinutes(10).ToUnixTimeSeconds()}}
            }
            """;
        var key = new RsaSecurityKey(signingKey) { KeyId = SigningKeyId };
        return new JsonWebTokenHandler().CreateToken(
            payload,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
    }

    private static string Metadata(string issuer, string origin, string tenant) =>
        $$"""
        {
          "issuer": "{{issuer}}",
          "authorization_endpoint": "{{origin}}/{{tenant}}/oauth2/v2.0/authorize",
          "token_endpoint": "{{origin}}/{{tenant}}/oauth2/v2.0/token",
          "jwks_uri": "{{origin}}/{{tenant}}/discovery/v2.0/keys",
          "end_session_endpoint": "{{origin}}/{{tenant}}/oauth2/v2.0/logout",
          "response_types_supported": ["code"],
          "subject_types_supported": ["pairwise"],
          "id_token_signing_alg_values_supported": ["RS256"]
        }
        """;

    private static string KeySet(RSA signingKey)
    {
        RSAParameters parameters = signingKey.ExportParameters(
            includePrivateParameters: false);
        return $$"""
            {"keys":[{"kty":"RSA","kid":"{{SigningKeyId}}","use":"sig","alg":"RS256","n":"{{Base64UrlEncoder.Encode(parameters.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent!)}}"}]}
            """;
    }

    /// <summary>
    /// The page the visitor actually sees at the provider. The identity is
    /// typed rather than configured so one run can consent as a member, as an
    /// allowlisted owner, and as an identity from the wrong directory, without
    /// any shared state between the cases.
    /// </summary>
    private static string ConsentPage(
        string requestId,
        string tenant,
        Guid directoryTenantId) =>
        $"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><title>Stub identity provider</title></head>
        <body>
        <h1>Stub identity provider</h1>
        <form method="post" action="/{tenant}/oauth2/v2.0/authorize">
        <input type="hidden" name="request" value="{HtmlEncoder.Default.Encode(requestId)}">
        <p><label for="oid">Object identifier</label>
        <input id="oid" name="oid" value=""></p>
        <p><label for="tid">Directory tenant</label>
        <input id="tid" name="tid" value="{directoryTenantId:D}"></p>
        <p><label for="email">Email</label>
        <input id="email" name="email" value="directory-person@e2e.invalid"></p>
        <p><label for="name">Display name</label>
        <input id="name" name="name" value="Directory Person"></p>
        <p><button type="submit" name="decision" value="allow">Continue</button>
        <button type="submit" name="decision" value="deny">Cancel</button></p>
        </form>
        </body>
        </html>
        """;

    private sealed record PendingAuthorization(
        string State,
        string Nonce,
        string CodeChallenge,
        string RedirectUri);

    private sealed record DirectoryGrant(
        string Nonce,
        string CodeChallenge,
        string RedirectUri,
        Guid ObjectId,
        Guid DirectoryTenantId,
        string Email,
        string DisplayName);
}
