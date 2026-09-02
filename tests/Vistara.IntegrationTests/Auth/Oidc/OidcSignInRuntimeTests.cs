using System.Net;
using System.Text.Json;
using Vistara.Auth.Cookies;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Oidc;

/// <summary>
/// Drives hosted OpenID Connect sign-in end to end against a live HTTPS
/// loopback identity provider and the production API composition over real
/// SQLite.
///
/// The behaviour under test is the whole path a browser takes: the
/// authorization request the API builds, the single-use login request it
/// stores, the server-to-server token exchange, identity-token validation
/// against the configured directory, the two allowlists, and the handoff into
/// the existing Vistara cookie session. Every refusal has to look the same to
/// the browser, and nothing secret may appear in a redirect, a cookie, or a
/// body.
/// </summary>
[Collection("OidcSignIn")]
public sealed class OidcSignInRuntimeTests
{
    private static readonly Guid MemberObjectId =
        Guid.Parse("11111111-2222-3333-4444-555555555501");

    private static readonly Guid AllowedOwnerObjectId =
        Guid.Parse("11111111-2222-3333-4444-555555555502");

    private static readonly Guid SecondAllowedOwnerObjectId =
        Guid.Parse("11111111-2222-3333-4444-555555555503");

    private static readonly Guid StrangerObjectId =
        Guid.Parse("11111111-2222-3333-4444-555555555504");

    private const string FailureLocation = "/login?error=oidc_sign_in_failed";

    /// <summary>
    /// The start route is a browser navigation, so everything that binds the
    /// sign-in has to be either in the redirect the provider needs or in the
    /// server-side request. The code verifier in particular must never leave
    /// the server, and the handle cookie must carry no session authority.
    /// </summary>
    [Fact]
    public async Task Start_builds_a_pkce_authorization_request_and_stores_it_server_side()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();

        StartedSignIn started = await host.StartAsync(returnTo: "/gallery?view=grid");

        Assert.Equal(HttpStatusCode.Found, started.Response.StatusCode);
        Assert.Equal("no-store", started.Response.CacheControl);
        IReadOnlyDictionary<string, string> request =
            LoopbackIdentityProvider.ReadAuthorizationRequest(started.AuthorizationUri);
        Assert.Equal(OidcSignInHost.ClientId, request["client_id"]);
        Assert.Equal("code", request["response_type"]);
        Assert.Equal("S256", request["code_challenge_method"]);
        Assert.Equal(
            $"https://{OidcSignInHost.ApplicationHost}/api/v1/auth/oidc/entra/callback",
            request["redirect_uri"]);
        Assert.NotEmpty(request["state"]);
        Assert.NotEmpty(request["nonce"]);
        Assert.NotEmpty(request["code_challenge"]);
        Assert.False(request.ContainsKey("code_verifier"));
        Assert.False(request.ContainsKey("client_secret"));
        Assert.DoesNotContain(
            "offline_access",
            request["scope"],
            StringComparison.Ordinal);

        Assert.NotNull(started.HandleCookie);
        Assert.DoesNotContain(
            request["state"],
            started.HandleCookie!,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            request["nonce"],
            started.HandleCookie!,
            StringComparison.Ordinal);
        Assert.Equal(1, await host.CountLoginRequestsAsync());
    }

    [Fact]
    public async Task An_existing_directory_member_receives_a_vistara_cookie_session()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);

        StartedSignIn started = await host.StartAsync(returnTo: "/gallery");
        string code = host.IdentityProvider.Authorize(
            started.AuthorizationUri,
            MemberObjectId);
        TestResponse callback = await host.CallbackAsync(
            code,
            started.State,
            started.HandleCookie);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/gallery", callback.Location);
        Assert.True(callback.ClearsCookie("__Host-vistara-oidc"));
        string session = Assert.IsType<string>(
            callback.CookieValue(CookieAuthOptions.ProductionCookieName));

        // Nothing from the exchange may travel back to the browser.
        Assert.DoesNotContain(code, callback.Location, StringComparison.Ordinal);
        Assert.DoesNotContain(code, callback.Body, StringComparison.Ordinal);
        Assert.All(
            callback.SetCookies,
            cookie => Assert.DoesNotContain(code, cookie, StringComparison.Ordinal));

        TestResponse me = await host.GetCurrentUserAsync(session);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        JsonElement body = me.Json();
        Assert.Equal("cookie", body.GetProperty("authenticationKind").GetString());
        Assert.Equal(userId, body.GetProperty("userId").GetGuid());
    }

    /// <summary>
    /// The exchange is server to server and must present a client credential in
    /// the request body. The federated managed-identity assertion is the
    /// production path, so it is exercised rather than only the secret.
    /// </summary>
    [Fact]
    public async Task The_token_exchange_presents_a_federated_client_assertion()
    {
        await using OidcSignInHost host =
            await OidcSignInHost.CreateAsync(useManagedIdentity: true);
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);

        TestResponse callback = await host.SignInAsync(MemberObjectId);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal("/", callback.Location);
        IReadOnlyDictionary<string, string> exchange =
            Assert.Single(host.IdentityProvider.TokenRequests);
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal(
            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            exchange["client_assertion_type"]);
        Assert.NotEmpty(exchange["client_assertion"]);
        Assert.NotEmpty(exchange["code_verifier"]);
        Assert.False(exchange.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task The_token_exchange_falls_back_to_the_configured_secret()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);

        TestResponse callback = await host.SignInAsync(MemberObjectId);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        IReadOnlyDictionary<string, string> exchange =
            Assert.Single(host.IdentityProvider.TokenRequests);
        Assert.Equal(OidcSignInHost.ClientSecret, exchange["client_secret"]);
        Assert.False(exchange.ContainsKey("client_assertion"));
        Assert.DoesNotContain(
            OidcSignInHost.ClientSecret,
            callback.Body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The only way a directory identity becomes an owner is the exact
    /// tenant-and-object allowlist, and only while the database-enforced
    /// bootstrap singleton is still open.
    /// </summary>
    [Fact]
    public async Task An_allowlisted_identity_claims_the_bootstrap_singleton()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);

        TestResponse first = await host.SignInAsync(AllowedOwnerObjectId);

        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal("/", first.Location);
        Assert.NotNull(first.CookieValue(CookieAuthOptions.ProductionCookieName));
        Assert.Equal(1, await host.CountTenantsAsync());

        // Signing in again resolves the identity that now exists rather than
        // provisioning a second owner.
        TestResponse second = await host.SignInAsync(AllowedOwnerObjectId);

        Assert.Equal(HttpStatusCode.Found, second.StatusCode);
        Assert.Equal("/", second.Location);
        Assert.NotNull(second.CookieValue(CookieAuthOptions.ProductionCookieName));
        Assert.Equal(1, await host.CountTenantsAsync());
    }

    /// <summary>
    /// Two allowlisted identities racing for the singleton must produce exactly
    /// one owner. The winner is decided by the database, so the loser has to
    /// fail closed rather than create a second tenant.
    /// </summary>
    [Fact]
    public async Task Concurrent_bootstrap_sign_ins_produce_exactly_one_owner()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId, SecondAllowedOwnerObjectId]);
        StartedSignIn first = await host.StartAsync();
        StartedSignIn second = await host.StartAsync();
        string firstCode = host.IdentityProvider.Authorize(
            first.AuthorizationUri,
            AllowedOwnerObjectId,
            email: "first@example.test");
        string secondCode = host.IdentityProvider.Authorize(
            second.AuthorizationUri,
            SecondAllowedOwnerObjectId,
            email: "second@example.test");

        TestResponse[] responses = await Task.WhenAll(
            host.CallbackAsync(firstCode, first.State, first.HandleCookie),
            host.CallbackAsync(secondCode, second.State, second.HandleCookie));

        Assert.Equal(1, await host.CountTenantsAsync());
        Assert.Equal(
            1,
            responses.Count(response =>
                response.CookieValue(CookieAuthOptions.ProductionCookieName) is not null));
        TestResponse loser = responses.Single(response =>
            response.CookieValue(CookieAuthOptions.ProductionCookieName) is null);
        Assert.Equal(FailureLocation, loser.Location);
    }

    [Fact]
    public async Task An_identity_outside_the_allowlist_cannot_bootstrap()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);

        TestResponse callback = await host.SignInAsync(StrangerObjectId);

        AssertRefused(callback);
        Assert.Equal(0, await host.CountTenantsAsync());
    }

    /// <summary>
    /// An identity token from a directory the deployment was not configured
    /// with is refused even when its object identifier is allowlisted, so a
    /// second tenant cannot mint an owner by reusing the same object id.
    /// </summary>
    [Fact]
    public async Task An_identity_from_another_directory_tenant_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);

        TestResponse callback = await host.SignInAsync(
            AllowedOwnerObjectId,
            directoryTenantId: OidcSignInHost.ForeignDirectoryTenantId);

        AssertRefused(callback);
        Assert.Equal(0, await host.CountTenantsAsync());
    }

    [Fact]
    public async Task A_directory_identity_that_is_not_a_member_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        await host.ProvisionLocalOwnerAsync();

        TestResponse callback = await host.SignInAsync(StrangerObjectId);

        AssertRefused(callback);
    }

    [Fact]
    public async Task A_replayed_state_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        StartedSignIn started = await host.StartAsync();
        string code = host.IdentityProvider.Authorize(
            started.AuthorizationUri,
            MemberObjectId);

        TestResponse first = await host.CallbackAsync(
            code,
            started.State,
            started.HandleCookie);
        TestResponse replay = await host.CallbackAsync(
            code,
            started.State,
            started.HandleCookie);

        Assert.Equal("/", first.Location);
        AssertRefused(replay);
    }

    [Fact]
    public async Task An_expired_login_request_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        StartedSignIn started = await host.StartAsync();
        string code = host.IdentityProvider.Authorize(
            started.AuthorizationUri,
            MemberObjectId);

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        TestResponse callback = await host.CallbackAsync(
            code,
            started.State,
            started.HandleCookie);

        AssertRefused(callback);
    }

    /// <summary>
    /// The state alone is not enough: the callback must arrive in the browser
    /// that started the sign-in, which is what the handle cookie proves.
    /// </summary>
    [Fact]
    public async Task A_handle_cookie_from_another_browser_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        StartedSignIn victim = await host.StartAsync();
        StartedSignIn attacker = await host.StartAsync();
        string code = host.IdentityProvider.Authorize(
            victim.AuthorizationUri,
            MemberObjectId);

        TestResponse callback = await host.CallbackAsync(
            code,
            victim.State,
            attacker.HandleCookie);

        AssertRefused(callback);
    }

    [Fact]
    public async Task A_missing_handle_cookie_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        StartedSignIn started = await host.StartAsync();
        string code = host.IdentityProvider.Authorize(
            started.AuthorizationUri,
            MemberObjectId);

        TestResponse callback = await host.CallbackAsync(
            code,
            started.State,
            handleCookie: null);

        AssertRefused(callback);
    }

    /// <summary>
    /// A cancelled consent is reported by the provider as an error parameter.
    /// It must consume the login request like any other outcome, and its
    /// description must never be echoed.
    /// </summary>
    [Fact]
    public async Task A_provider_reported_error_is_refused_and_consumes_the_request()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        StartedSignIn started = await host.StartAsync();
        string code = host.IdentityProvider.Authorize(
            started.AuthorizationUri,
            MemberObjectId);

        TestResponse denied = await host.CallbackAsync(
            code: null,
            started.State,
            started.HandleCookie,
            error: "access_denied");
        TestResponse afterwards = await host.CallbackAsync(
            code,
            started.State,
            started.HandleCookie);

        AssertRefused(denied);
        Assert.DoesNotContain("access_denied", denied.Location, StringComparison.Ordinal);
        AssertRefused(afterwards);
        Assert.Empty(host.IdentityProvider.TokenRequests);
    }

    [Fact]
    public async Task An_authorization_code_the_provider_does_not_know_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        StartedSignIn started = await host.StartAsync();
        _ = host.IdentityProvider.Authorize(started.AuthorizationUri, MemberObjectId);

        TestResponse callback = await host.CallbackAsync(
            "code-that-was-never-issued",
            started.State,
            started.HandleCookie);

        AssertRefused(callback);
    }

    /// <summary>
    /// A token signed with a key the provider never published is a forgery,
    /// even when every claim in it is correct.
    /// </summary>
    [Fact]
    public async Task An_identity_token_signed_by_an_unpublished_key_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);
        StartedSignIn started = await host.StartAsync();
        LoopbackIdentityProvider.AuthorizationGrant grant =
            host.IdentityProvider.DescribeGrant(
                started.AuthorizationUri,
                AllowedOwnerObjectId);
        host.IdentityProvider.TokenResponseBody = () =>
            LoopbackIdentityProvider.TokenResponse(
                host.IdentityProvider.CreateIdToken(
                    grant,
                    signingKeyOverride: host.IdentityProvider.UnpublishedSigningKey));

        TestResponse callback = await host.CallbackAsync(
            "any-code",
            started.State,
            started.HandleCookie);

        AssertRefused(callback);
        Assert.Equal(0, await host.CountTenantsAsync());
    }

    [Fact]
    public async Task An_identity_token_for_another_audience_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);
        StartedSignIn started = await host.StartAsync();
        LoopbackIdentityProvider.AuthorizationGrant grant =
            host.IdentityProvider.DescribeGrant(
                started.AuthorizationUri,
                AllowedOwnerObjectId);
        host.IdentityProvider.TokenResponseBody = () =>
            LoopbackIdentityProvider.TokenResponse(
                host.IdentityProvider.CreateIdToken(
                    grant,
                    audienceOverride: "22222222-3333-4444-5555-666666666666"));

        TestResponse callback = await host.CallbackAsync(
            "any-code",
            started.State,
            started.HandleCookie);

        AssertRefused(callback);
    }

    /// <summary>
    /// A key set mixing symmetric material, an encryption key, an undersized
    /// modulus, an unidentified key, and a downgraded algorithm must leave the
    /// provider with no usable signing key at all.
    /// </summary>
    [Fact]
    public async Task A_hostile_key_set_cannot_sign_an_accepted_token()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);
        host.IdentityProvider.PublishHostileKeySetOnly = true;

        StartedSignIn started = await host.StartAsync();

        Assert.Equal(FailureLocation, started.Response.Location);
        Assert.Null(started.HandleCookie);
        Assert.Equal(0, await host.CountTenantsAsync());
    }

    [Fact]
    public async Task A_token_response_that_is_not_a_token_is_refused()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        StartedSignIn started = await host.StartAsync();
        host.IdentityProvider.TokenResponseBody = () =>
            """{"token_type":"Bearer","access_token":"opaque"}""";

        TestResponse callback = await host.CallbackAsync(
            "any-code",
            started.State,
            started.HandleCookie);

        AssertRefused(callback);
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        await host.DisableUserAsync(tenantId, userId);

        TestResponse callback = await host.SignInAsync(MemberObjectId);

        AssertRefused(callback);
    }

    /// <summary>
    /// A user who belongs to several tenants still receives exactly one
    /// session, bound to one tenant, because a browser session is single
    /// tenant by construction.
    /// </summary>
    [Fact]
    public async Task A_user_with_several_memberships_receives_one_session()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        Guid secondTenantId = await host.AddMembershipAsync(userId, "beta");

        TestResponse callback = await host.SignInAsync(MemberObjectId);

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        string session = Assert.IsType<string>(
            callback.CookieValue(CookieAuthOptions.ProductionCookieName));
        JsonElement me = (await host.GetCurrentUserAsync(session)).Json();
        Guid signedInTenant = me.GetProperty("tenantId").GetGuid();
        Assert.Contains(signedInTenant, new[] { tenantId, secondTenantId });
    }

    /// <summary>
    /// A hosted sign-in must not leave the previous session alive next to the
    /// new one; the browser ends up holding exactly one credential.
    /// </summary>
    [Fact]
    public async Task A_prior_session_is_revoked_when_a_hosted_sign_in_succeeds()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string previous = await host.LoginLocallyAsync("local-owner@example.test");

        TestResponse callback = await host.SignInAsync(
            MemberObjectId,
            sessionCookie: previous);

        string session = Assert.IsType<string>(
            callback.CookieValue(CookieAuthOptions.ProductionCookieName));
        Assert.NotEqual(previous, session);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.GetCurrentUserAsync(previous)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetCurrentUserAsync(session)).StatusCode);
    }

    /// <summary>
    /// Entra drives front-channel sign-out as a cross-site GET inside an
    /// iframe. The Vistara session cookie is SameSite=Lax, so it is not
    /// attached to that request and the endpoint has neither a session to
    /// revoke nor a way to learn which one to revoke. It must therefore leave
    /// every session exactly as it found it.
    /// </summary>
    [Fact]
    public async Task Front_channel_logout_leaves_every_session_alone()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string session = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));
        int liveBefore = await host.CountLiveSessionsAsync();

        TestResponse response = await host.FrontChannelLogoutAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(string.Empty, response.Body);
        Assert.Empty(response.SetCookies);
        Assert.Equal(liveBefore, await host.CountLiveSessionsAsync());
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetCurrentUserAsync(session)).StatusCode);
    }

    /// <summary>
    /// The obvious way to make an iframe sign-out "work" is to accept a
    /// session identifier from the query string. That would be an
    /// unauthenticated revocation oracle for any session an attacker can
    /// name, so nothing in the request may influence any state.
    /// </summary>
    [Fact]
    public async Task Front_channel_logout_cannot_revoke_a_named_session()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string session = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));
        int liveBefore = await host.CountLiveSessionsAsync();

        foreach (string query in new[]
        {
            $"?sid={Uri.EscapeDataString(session)}",
            $"?session={Uri.EscapeDataString(session)}",
            $"?token={Uri.EscapeDataString(session)}",
            "?sid=00000000-0000-0000-0000-000000000000&iss=https%3A%2F%2Fattacker.example",
        })
        {
            TestResponse response = await host.FrontChannelLogoutAsync(query);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(string.Empty, response.Body);
            Assert.Empty(response.SetCookies);
        }

        Assert.Equal(liveBefore, await host.CountLiveSessionsAsync());
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetCurrentUserAsync(session)).StatusCode);
        Assert.DoesNotContain(
            "sign",
            string.Join("\n", host.AuditRecords.Where(
                record => record.Contains("logout", StringComparison.OrdinalIgnoreCase))),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Revocation lives where the session cookie actually arrives: a same-site
    /// POST covered by the antiforgery policy. It revokes first and only then
    /// reports where the provider session can be ended, and that URL is built
    /// from discovered metadata and the registered reply URL alone.
    /// </summary>
    [Fact]
    public async Task Relying_party_sign_out_revokes_then_points_at_the_provider()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string session = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));

        TestResponse response = await host.SignOutAsync(session);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.ClearsCookie(CookieAuthOptions.ProductionCookieName));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.GetCurrentUserAsync(session)).StatusCode);

        string endSessionUrl = Assert.IsType<string>(
            response.Json().GetProperty("endSessionUrl").GetString());
        var parsed = new Uri(endSessionUrl);
        Assert.Equal(host.IdentityProvider.Authority.Host, parsed.Host);
        Assert.Contains(
            Uri.EscapeDataString(
                $"https://{OidcSignInHost.ApplicationHost}/api/v1/auth/oidc/entra/signed-out"),
            parsed.Query,
            StringComparison.Ordinal);
        Assert.DoesNotContain(session, endSessionUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cookie authentication rotates a session once the sliding refresh
    /// interval has elapsed: the presented token is revoked and a refreshed
    /// one is appended to the very response the sign-out is answering. Acting
    /// on the presented token would revoke a row that is already gone and
    /// leave the refreshed session live, handing the caller a working cookie
    /// from their own sign-out. Sign-out must act on the effective session and
    /// the response must carry only the deletion.
    /// </summary>
    [Fact]
    public async Task Relying_party_sign_out_revokes_a_session_rotated_by_this_request()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string original = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));
        string csrfToken = Assert.IsType<string>(
            (await host.GetCurrentUserAsync(original)).Json()
                .GetProperty("csrfToken")
                .GetString());
        Assert.Equal(1, await host.CountLiveSessionsAsync());

        host.Clock.Advance(
            host.CookieOptions.SlidingRefreshInterval + TimeSpan.FromMinutes(1));

        TestResponse response = await host.SignOutAsync(original, csrfToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Json().GetProperty("endSessionUrl").GetString());

        // The refreshed cookie must not survive as an instruction the browser
        // could keep, and no session may remain live behind it.
        string session = Assert.IsType<string>(
            Assert.Single(
                response.SetCookies,
                cookie => cookie.StartsWith(
                    CookieAuthOptions.ProductionCookieName + "=",
                    StringComparison.Ordinal)));
        Assert.Contains("Max-Age=0", session, StringComparison.Ordinal);
        Assert.Equal(0, await host.CountLiveSessionsAsync());

        string? refreshed = response.CookieValue(CookieAuthOptions.ProductionCookieName);
        Assert.Null(refreshed);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.GetCurrentUserAsync(original)).StatusCode);

        string audit = string.Join("\n", host.AuditRecords);
        Assert.Contains("session_revoked", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "no_live_session_to_revoke",
            audit,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The audit answers whether a session was actually revoked, so a sign-out
    /// that found nothing to revoke must not record one that succeeded.
    /// </summary>
    [Fact]
    public async Task Relying_party_sign_out_does_not_audit_a_revocation_that_did_not_happen()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string session = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));
        string csrfToken = Assert.IsType<string>(
            (await host.GetCurrentUserAsync(session)).Json()
                .GetProperty("csrfToken")
                .GetString());

        _ = await host.SignOutAsync(session, csrfToken);
        int auditedBefore = host.AuditRecords.Count(
            record => record.Contains("session_revoked", StringComparison.Ordinal));
        Assert.Equal(1, auditedBefore);
        Assert.Equal(0, await host.CountLiveSessionsAsync());

        // The browser still holds the cookie, so the request is well formed
        // and answered. It simply revoked nothing, and the audit has to say so
        // rather than record a second sign-out.
        TestResponse repeated = await host.SignOutAsync(session, csrfToken);

        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        Assert.True(repeated.ClearsCookie(CookieAuthOptions.ProductionCookieName));
        Assert.Equal(0, await host.CountLiveSessionsAsync());
        Assert.Equal(
            auditedBefore,
            host.AuditRecords.Count(
                record => record.Contains("session_revoked", StringComparison.Ordinal)));
        Assert.Contains(
            "no_live_session_to_revoke",
            string.Join("\n", host.AuditRecords),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Sign-out acts on the session the caller presents, so an anonymous or
    /// forged cross-site POST has nothing to act on and learns nothing.
    /// </summary>
    [Fact]
    public async Task Relying_party_sign_out_refuses_a_caller_with_no_session()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();
        Guid userId = await host.ReadOwnerUserIdAsync(tenantId);
        await host.LinkDirectoryIdentityAsync(tenantId, userId, MemberObjectId);
        string session = Assert.IsType<string>(
            (await host.SignInAsync(MemberObjectId))
                .CookieValue(CookieAuthOptions.ProductionCookieName));
        int liveBefore = await host.CountLiveSessionsAsync();

        TestResponse anonymous = await host.SendAsync(
            "POST",
            "/api/v1/auth/oidc/entra/sign-out");
        TestResponse forged = await host.SignOutAsync(session, csrfToken: "not-the-token");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(liveBefore, await host.CountLiveSessionsAsync());
        Assert.Equal(
            HttpStatusCode.OK,
            (await host.GetCurrentUserAsync(session)).StatusCode);
    }

    /// <summary>
    /// Local password sign-out is unchanged and remains the revocation path a
    /// deployment without hosted sign-in relies on.
    /// </summary>
    [Fact]
    public async Task Local_logout_still_revokes_the_session()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        await host.ProvisionLocalOwnerAsync();
        string session = await host.LoginLocallyAsync("local-owner@example.test");

        TestResponse response = await host.SendAsync(
            "POST",
            "/api/v1/auth/logout",
            sessionCookie: session);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.ClearsCookie(CookieAuthOptions.ProductionCookieName));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.GetCurrentUserAsync(session)).StatusCode);
    }

    /// <summary>
    /// The signed-out landing route is a registered reply URL that a provider
    /// navigates to with parameters of its own choosing, none of which may
    /// become a redirect target.
    /// </summary>
    [Fact]
    public async Task The_signed_out_route_ignores_what_the_provider_appended()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();

        TestResponse response = await host.SendAsync(
            "GET",
            "/api/v1/auth/oidc/entra/signed-out",
            "?post_logout_redirect_uri=https%3A%2F%2Fattacker.example");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/login", response.Location);
    }

    /// <summary>
    /// The return target comes from the browser, so a candidate that would
    /// leave the application origin must never become the redirect the session
    /// is issued with.
    /// </summary>
    [Theory]
    [InlineData("https://attacker.example/steal")]
    [InlineData("//attacker.example/steal")]
    [InlineData("/\\attacker.example")]
    public async Task A_hostile_return_target_never_becomes_a_redirect(string candidate)
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();

        StartedSignIn started = await host.StartAsync(returnTo: candidate);

        Assert.Equal(FailureLocation, started.Response.Location);
        Assert.DoesNotContain(
            "attacker.example",
            started.Response.Location,
            StringComparison.Ordinal);
        Assert.Equal(0, await host.CountLoginRequestsAsync());
    }

    [Fact]
    public async Task An_unknown_provider_key_starts_nothing()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();

        StartedSignIn started = await host.StartAsync(providerId: "okta");

        Assert.Equal(FailureLocation, started.Response.Location);
        Assert.Null(started.HandleCookie);
        Assert.True(started.Response.ClearsCookie("__Host-vistara-oidc"));
        Assert.Equal(0, await host.CountLoginRequestsAsync());
    }

    /// <summary>
    /// The anonymous setup read is how a first-run client learns a hosted
    /// provider exists, and it must publish nothing else about it.
    /// </summary>
    [Fact]
    public async Task The_setup_surface_publishes_the_configured_provider()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();

        JsonElement body = (await host.DescribeSetupAsync()).Json();

        JsonElement provider = Assert.Single(
            body.GetProperty("signInProviders").EnumerateArray());
        Assert.Equal("entra", provider.GetProperty("id").GetString());
        Assert.Equal(
            "Microsoft Entra ID",
            provider.GetProperty("displayName").GetString());
        Assert.Equal(
            "/api/v1/auth/oidc/entra/start",
            provider.GetProperty("startUrl").GetString());
        Assert.DoesNotContain(
            OidcSignInHost.ClientId,
            body.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            OidcSignInHost.DirectoryTenantId.ToString("D"),
            body.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            OidcSignInHost.ClientSecret,
            body.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Composing hosted sign-in must not change local setup or password
    /// recovery for a deployment that never uses it.
    /// </summary>
    [Fact]
    public async Task Local_setup_and_password_sign_in_are_unchanged()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync();
        Guid tenantId = await host.ProvisionLocalOwnerAsync();

        string session = await host.LoginLocallyAsync("local-owner@example.test");
        JsonElement me = (await host.GetCurrentUserAsync(session)).Json();

        Assert.Equal("cookie", me.GetProperty("authenticationKind").GetString());
        Assert.Equal(tenantId, me.GetProperty("tenantId").GetGuid());
        Assert.False(
            (await host.DescribeSetupAsync()).Json().GetProperty("available").GetBoolean());
    }

    /// <summary>
    /// The browser is told one thing and the operator is told another. Every
    /// refusal produces the same redirect, while the server-side audit
    /// distinguishes a replay from a rejected directory from an identity that
    /// is simply not a member - and records none of the secrets that produced
    /// the decision.
    /// </summary>
    [Fact]
    public async Task The_audit_distinguishes_what_the_browser_never_learns()
    {
        await using OidcSignInHost host = await OidcSignInHost.CreateAsync(
            bootstrapEnabled: true,
            allowedObjectIds: [AllowedOwnerObjectId]);

        StartedSignIn replayed = await host.StartAsync();
        string replayedCode = host.IdentityProvider.Authorize(
            replayed.AuthorizationUri,
            AllowedOwnerObjectId);
        _ = await host.CallbackAsync(
            replayedCode,
            replayed.State,
            replayed.HandleCookie);
        TestResponse replay = await host.CallbackAsync(
            replayedCode,
            replayed.State,
            replayed.HandleCookie);

        StartedSignIn denied = await host.StartAsync();
        TestResponse cancelled = await host.CallbackAsync(
            code: null,
            denied.State,
            denied.HandleCookie,
            error: "access_denied");

        StartedSignIn foreign = await host.StartAsync();
        string foreignCode = host.IdentityProvider.Authorize(
            foreign.AuthorizationUri,
            AllowedOwnerObjectId,
            OidcSignInHost.ForeignDirectoryTenantId);
        TestResponse wrongDirectory = await host.CallbackAsync(
            foreignCode,
            foreign.State,
            foreign.HandleCookie);

        TestResponse stranger = await host.SignInAsync(StrangerObjectId);

        // One vocabulary for the browser.
        Assert.Equal(FailureLocation, replay.Location);
        Assert.Equal(FailureLocation, cancelled.Location);
        Assert.Equal(FailureLocation, wrongDirectory.Location);
        Assert.Equal(FailureLocation, stranger.Location);

        // Several for the operator.
        string audit = string.Join("\n", host.AuditRecords);
        Assert.Contains("state_unknown_expired_or_replayed", audit, StringComparison.Ordinal);
        Assert.Contains("provider_reported_error", audit, StringComparison.Ordinal);
        Assert.Contains("id_token_directory_rejected", audit, StringComparison.Ordinal);
        Assert.Contains(
            "identity_not_a_member_and_not_allowlisted",
            audit,
            StringComparison.Ordinal);
        Assert.Contains("first_owner_provisioned", audit, StringComparison.Ordinal);
        Assert.Contains("session_issued", audit, StringComparison.Ordinal);

        // And no secret in either place.
        foreach (string secret in new[]
        {
            replayed.State,
            foreign.State,
            replayedCode,
            foreignCode,
            replayed.HandleCookie!,
            OidcSignInHost.ClientSecret,
            LoopbackIdentityProvider.ReadAuthorizationRequest(
                replayed.AuthorizationUri)["nonce"],
        })
        {
            Assert.DoesNotContain(secret, audit, StringComparison.Ordinal);
        }
    }

    private static void AssertRefused(TestResponse callback)
    {
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal(FailureLocation, callback.Location);
        Assert.Equal(string.Empty, callback.Body);
        Assert.Null(callback.CookieValue(CookieAuthOptions.ProductionCookieName));
        Assert.True(callback.ClearsCookie("__Host-vistara-oidc"));
    }
}
