using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Vistara.Api.Features.Account;
using Vistara.Api.Features.Oidc;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Tenancy;

namespace Vistara.Api.Composition.Platform;

/// <summary>
/// The transient state one in-flight sign-in keeps in the browser: which
/// provider started it, the handle that binds the callback to this user agent,
/// and the nonce the identity token must carry. It is only ever seen protected.
/// </summary>
public sealed record OidcHandlePayload(string ProviderId, string Handle, string Nonce)
{
    public override string ToString() =>
        $"{nameof(OidcHandlePayload)} {{ ProviderId = {ProviderId}, Handle = [REDACTED], Nonce = [REDACTED] }}";
}

/// <summary>
/// Protects the in-flight login state carried by the browser.
/// </summary>
public interface IOidcHandleProtector
{
    string Protect(OidcHandlePayload payload, DateTimeOffset expiresAtUtc);

    bool TryUnprotect(string? protectedValue, out OidcHandlePayload payload);
}

/// <summary>
/// Uses the shared Data Protection key ring, so every hosted replica can read
/// a handle any other replica issued and none of them stores the nonce in the
/// database. The payload is time limited by the same key ring, which means an
/// expired cookie fails cryptographically before it is ever parsed.
/// </summary>
internal sealed class DataProtectionOidcHandleProtector : IOidcHandleProtector
{
    internal const string Purpose = "Vistara.Api.Oidc.LoginHandle.v1";
    private const char Separator = '\n';

    private readonly ITimeLimitedDataProtector _protector;

    public DataProtectionOidcHandleProtector(IDataProtectionProvider dataProtection)
    {
        ArgumentNullException.ThrowIfNull(dataProtection);
        _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
    }

    public string Protect(OidcHandlePayload payload, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _protector.Protect(
            string.Join(Separator, payload.ProviderId, payload.Handle, payload.Nonce),
            expiresAtUtc);
    }

    public bool TryUnprotect(string? protectedValue, out OidcHandlePayload payload)
    {
        payload = new OidcHandlePayload(string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return false;
        }

        string unprotected;
        try
        {
            unprotected = _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // A tampered, foreign, or expired handle is indistinguishable here
            // on purpose: all three are simply not a handle this API issued.
            return false;
        }
        catch (FormatException)
        {
            return false;
        }

        string[] parts = unprotected.Split(Separator);
        if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty))
        {
            return false;
        }

        payload = new OidcHandlePayload(parts[0], parts[1], parts[2]);
        return true;
    }
}

/// <summary>
/// Writes the detailed sign-in outcome to the application log. The browser
/// only sees one uniform failure code, so this is where an operator finds out
/// whether a sign-in was cancelled, replayed, or refused by an allowlist. No
/// authorization code, token, state, nonce, or verifier is ever passed here.
/// </summary>
internal sealed class PlatformOidcAuditSink(ILogger<PlatformOidcAuditSink> logger) :
    IOidcAuditSink
{
    private static readonly Action<ILogger, string, string, string, string, string, Exception?> Recorded =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Information,
            new EventId(6100, "OidcSignInOutcome"),
            "OIDC {Stage} for provider {ProviderId}: {Detail} (directory {DirectoryTenantId}, object {ObjectId})");

    public void Record(OidcAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        Recorded(
            logger,
            auditEvent.Stage,
            auditEvent.ProviderId,
            auditEvent.Detail,
            auditEvent.DirectoryTenantId?.ToString("D") ?? "-",
            auditEvent.ObjectId ?? "-",
            null);
    }
}

/// <summary>
/// Revokes the Vistara browser session for provider-initiated sign-out. The
/// existing browser session port owns revocation, so front-channel logout
/// cannot drift from the local logout route.
/// </summary>
internal sealed class PlatformOidcLogoutAdapter(IBrowserSessionPort sessions) :
    IOidcLogoutPort
{
    public ValueTask<string> SignOutAsync(
        string? sessionToken,
        CancellationToken cancellationToken) =>
        sessions.LogoutAsync(sessionToken, cancellationToken);
}

/// <summary>
/// The server side of hosted OpenID Connect sign-in.
///
/// The flow never trusts the browser with anything but a protected handle: the
/// state, nonce, and code verifier live in the login request store or inside
/// the protected cookie, the authorization code is redeemed server to server
/// over a redirect-disabled client, and a Vistara session is only issued after
/// the identity token has been validated against the configured directory and
/// an active membership has been resolved. There is no path here that creates
/// a user from an email address, and no path that creates an owner outside the
/// database-enforced bootstrap singleton.
/// </summary>
internal sealed class PlatformOidcLoginAdapter : IOidcLoginPort
{
    private const string StartStage = "start";
    private const string CallbackStage = "callback";

    /// <summary>The number of expired login requests one sign-in may sweep.</summary>
    private const int SweepBatchSize = 32;

    private readonly PlatformOidcProviderRegistry _registry;
    private readonly PlatformFirstOwnerPolicy _firstOwner;
    private readonly RelationalOidcLoginRequestStore _requests;
    private readonly RelationalIdentityCatalog _catalog;
    private readonly RelationalFirstOwnerProvisioningStore _provisioning;
    private readonly PlatformLoginSessionFactory _sessionFactory;
    private readonly IBrowserSessionPort _sessions;
    private readonly TenantFactory _tenants;
    private readonly IdentityFactory _identities;
    private readonly IOidcHandleProtector _protector;
    private readonly IOidcRandomSource _randomSource;
    private readonly IOidcAuditSink _audit;
    private readonly IUuid7Generator _ids;
    private readonly IClock _clock;

    public PlatformOidcLoginAdapter(
        PlatformOidcProviderRegistry registry,
        PlatformFirstOwnerPolicy firstOwner,
        RelationalOidcLoginRequestStore requests,
        RelationalIdentityCatalog catalog,
        RelationalFirstOwnerProvisioningStore provisioning,
        PlatformLoginSessionFactory sessionFactory,
        IBrowserSessionPort sessions,
        TenantFactory tenants,
        IdentityFactory identities,
        IOidcHandleProtector protector,
        IOidcRandomSource randomSource,
        IOidcAuditSink audit,
        IUuid7Generator ids,
        IClock clock)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _firstOwner = firstOwner ?? throw new ArgumentNullException(nameof(firstOwner));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _provisioning = provisioning ?? throw new ArgumentNullException(nameof(provisioning));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _identities = identities ?? throw new ArgumentNullException(nameof(identities));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask<Result<OidcStartResult>> StartAsync(
        string providerId,
        string? returnTo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PlatformOidcProvider? provider = _registry.Find(providerId);
        if (provider is null)
        {
            return Fail<OidcStartResult>(
                providerId ?? string.Empty,
                StartStage,
                "provider_not_configured",
                OidcErrors.InvalidRequest);
        }

        Result<OidcLoginHandle> created = provider.LoginRequests.Create(returnTo);
        if (!created.TryGetValue(out OidcLoginHandle? handle))
        {
            return Fail<OidcStartResult>(
                provider.ProviderId,
                StartStage,
                "invalid_return_target",
                created.Error!);
        }

        Result<OidcProviderMetadata> discovered =
            await provider.Metadata.GetAsync(cancellationToken);
        if (!discovered.TryGetValue(out OidcProviderMetadata? metadata))
        {
            return Fail<OidcStartResult>(
                provider.ProviderId,
                StartStage,
                "metadata_unavailable",
                discovered.Error!);
        }

        // An abandoned sign-in leaves a row behind, so each new one clears a
        // bounded batch of requests that can no longer be consumed.
        _ = await _requests.DeleteExpiredAsync(
            _clock.UtcNow.ToUniversalTime(),
            SweepBatchSize,
            cancellationToken);

        string browserHandle = CreateBrowserHandle();
        bool stored = await _requests.CreateAsync(
            new OidcLoginRequest(
                Convert.FromHexString(handle.StateDigest),
                provider.ProviderId,
                Convert.FromHexString(handle.NonceDigest),
                Digest(browserHandle),
                handle.CodeVerifier,
                provider.Options.RedirectUri.AbsoluteUri,
                handle.ReturnTo,
                handle.CreatedAt,
                handle.ExpiresAt),
            cancellationToken);
        if (!stored)
        {
            return Fail<OidcStartResult>(
                provider.ProviderId,
                StartStage,
                "login_request_not_stored",
                OidcErrors.InvalidRequest);
        }

        _audit.Record(new OidcAuditEvent(provider.ProviderId, StartStage, "authorization_requested"));
        return Result.Success(new OidcStartResult(
            OidcAuthorizationUrlBuilder.Build(provider.Options, metadata, handle),
            OidcHandleCookie.ToSetCookieHeader(
                _protector.Protect(
                    new OidcHandlePayload(provider.ProviderId, browserHandle, handle.Nonce),
                    handle.ExpiresAt),
                provider.Options.LoginRequestLifetime)));
    }

    public async ValueTask<Result<OidcSignInResult>> CompleteAsync(
        OidcCallbackCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        PlatformOidcProvider? provider = _registry.Find(command.ProviderId);
        if (provider is null)
        {
            return Fail<OidcSignInResult>(
                command.ProviderId,
                CallbackStage,
                "provider_not_configured",
                OidcErrors.InvalidRequest);
        }

        // The state is consumed before anything else is considered, including a
        // provider-reported error, so one authorization request can only ever
        // be presented once whatever the outcome.
        if (!OidcHandleCryptography.TryComputeDigest(command.State, out string stateDigest))
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "state_malformed",
                OidcErrors.InvalidState);
        }

        ConsumedOidcLoginRequest? consumed = await _requests.ConsumeAsync(
            Convert.FromHexString(stateDigest),
            _clock.UtcNow.ToUniversalTime(),
            cancellationToken);
        if (consumed is null)
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "state_unknown_expired_or_replayed",
                OidcErrors.InvalidState);
        }

        Result<OidcHandlePayload> binding = BindRequest(provider, command, consumed);
        if (!binding.TryGetValue(out OidcHandlePayload? payload))
        {
            return Result.Failure<OidcSignInResult>(binding.Error!);
        }

        if (command.Error is not null)
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "provider_reported_error",
                OidcErrors.ProviderRejected);
        }

        if (!TryCreateRedemption(command.Code, consumed.CodeVerifier, out OidcAuthorizationCodeRedemption? redemption))
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "authorization_code_missing_or_malformed",
                OidcErrors.InvalidRequest);
        }

        Result<OidcProviderMetadata> discovered =
            await provider.Metadata.GetAsync(cancellationToken);
        if (!discovered.TryGetValue(out OidcProviderMetadata? metadata))
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "metadata_unavailable",
                discovered.Error!);
        }

        Result<OidcTokenSet> redeemed = await provider.TokenClient
            .RedeemAuthorizationCodeAsync(redemption!, metadata, cancellationToken);
        if (!redeemed.TryGetValue(out OidcTokenSet? tokens))
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "token_exchange_failed",
                redeemed.Error!);
        }

        Result<OidcIdentity> validated = await ValidateIdentityAsync(
            provider,
            metadata,
            tokens,
            payload!.Nonce,
            cancellationToken);
        if (!validated.TryGetValue(out OidcIdentity? identity))
        {
            return Result.Failure<OidcSignInResult>(validated.Error!);
        }

        return await SignInAsync(provider, identity, consumed, command, cancellationToken);
    }

    /// <summary>
    /// Validates the identity token against the nonce this browser was issued.
    /// A single forced metadata refresh covers a provider signing-key rotation
    /// that happened between discovery and this callback; the refresh is rate
    /// limited inside the cache, so it cannot be used to hammer the provider.
    /// </summary>
    private async ValueTask<Result<OidcIdentity>> ValidateIdentityAsync(
        PlatformOidcProvider provider,
        OidcProviderMetadata metadata,
        OidcTokenSet tokens,
        string expectedNonce,
        CancellationToken cancellationToken)
    {
        Result<OidcIdentity> validated = await provider.Validator.ValidateAsync(
            tokens.IdToken,
            new OidcIdTokenValidationContext(metadata, expectedNonce, tokens.AccessToken),
            cancellationToken);
        if (validated.IsSuccess)
        {
            return validated;
        }

        if (!string.Equals(
                validated.Error!.Code,
                OidcErrors.InvalidIdToken.Code,
                StringComparison.Ordinal))
        {
            return Fail<OidcIdentity>(
                provider.ProviderId,
                CallbackStage,
                "id_token_directory_rejected",
                validated.Error);
        }

        Result<OidcProviderMetadata> refreshed =
            await provider.Metadata.RefreshAsync(cancellationToken);
        if (!refreshed.TryGetValue(out OidcProviderMetadata? current) ||
            ReferenceEquals(current, metadata))
        {
            return Fail<OidcIdentity>(
                provider.ProviderId,
                CallbackStage,
                "id_token_invalid",
                OidcErrors.InvalidIdToken);
        }

        Result<OidcIdentity> retried = await provider.Validator.ValidateAsync(
            tokens.IdToken,
            new OidcIdTokenValidationContext(current, expectedNonce, tokens.AccessToken),
            cancellationToken);
        return retried.IsSuccess
            ? retried
            : Fail<OidcIdentity>(
                provider.ProviderId,
                CallbackStage,
                "id_token_invalid",
                retried.Error!);
    }

    /// <summary>
    /// Binds the consumed request to this browser, this provider, and this
    /// reply URL. Every mismatch is a different failure in the audit and the
    /// same refusal to the browser.
    /// </summary>
    private Result<OidcHandlePayload> BindRequest(
        PlatformOidcProvider provider,
        OidcCallbackCommand command,
        ConsumedOidcLoginRequest consumed)
    {
        if (!string.Equals(consumed.ProviderId, provider.ProviderId, StringComparison.Ordinal))
        {
            return Reject(provider.ProviderId, "login_request_provider_mismatch");
        }

        if (!string.Equals(
                consumed.RedirectUri,
                provider.Options.RedirectUri.AbsoluteUri,
                StringComparison.Ordinal))
        {
            return Reject(provider.ProviderId, "login_request_redirect_mismatch");
        }

        if (!_protector.TryUnprotect(command.HandleCookieValue, out OidcHandlePayload payload))
        {
            return Reject(provider.ProviderId, "handle_cookie_missing_or_unreadable");
        }

        if (!string.Equals(payload.ProviderId, provider.ProviderId, StringComparison.Ordinal))
        {
            return Reject(provider.ProviderId, "handle_cookie_provider_mismatch");
        }

        if (!RelationalOidcLoginRequestStore.HandleMatches(consumed, Digest(payload.Handle)))
        {
            return Reject(provider.ProviderId, "handle_cookie_mismatch");
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Digest(payload.Nonce),
                consumed.NonceDigest))
        {
            return Reject(provider.ProviderId, "handle_cookie_nonce_mismatch");
        }

        return Result.Success(payload);
    }

    private async ValueTask<Result<OidcSignInResult>> SignInAsync(
        PlatformOidcProvider provider,
        OidcIdentity identity,
        ConsumedOidcLoginRequest consumed,
        OidcCallbackCommand command,
        CancellationToken cancellationToken)
    {
        if (identity.DirectoryTenantId != provider.Options.DirectoryTenantId ||
            !Guid.TryParseExact(identity.ObjectId, "D", out Guid objectId) ||
            objectId == Guid.Empty)
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "directory_identity_not_accepted",
                OidcErrors.TenantNotAllowed);
        }

        // The stored key is the canonical (provider, directory tenant) issuer
        // and the object identifier. The token was already proved to come from
        // the configured authority, carry the configured audience, and name
        // this directory tenant, so the canonical form is a storage key rather
        // than a second authorization decision.
        string issuer = ExternalFirstOwnerCredential.CanonicalIssuer(
            provider.ProviderId,
            identity.DirectoryTenantId);
        string subject = ExternalFirstOwnerCredential.SubjectFor(objectId);

        ExternalIdentityLookupResult? existing =
            await _catalog.FindByExternalIdentityAsync(issuer, subject, cancellationToken);
        if (existing is null)
        {
            Result<ExternalIdentityLookupResult> provisioned = await ProvisionFirstOwnerAsync(
                provider,
                identity,
                objectId,
                issuer,
                subject,
                cancellationToken);
            if (!provisioned.TryGetValue(out existing))
            {
                return Result.Failure<OidcSignInResult>(provisioned.Error!);
            }
        }

        if (existing!.IsDisabled)
        {
            return Fail<OidcSignInResult>(
                provider.ProviderId,
                CallbackStage,
                "user_disabled",
                OidcErrors.ProviderRejected,
                identity.DirectoryTenantId,
                subject);
        }

        Result<string> issued = await IssueSessionAsync(
            provider,
            existing.UserId,
            command.ExistingSessionToken,
            identity.DirectoryTenantId,
            subject,
            cancellationToken);
        return issued.TryGetValue(out string? setCookieHeader)
            ? Result.Success(new OidcSignInResult(setCookieHeader, consumed.ReturnTo))
            : Result.Failure<OidcSignInResult>(issued.Error!);
    }

    /// <summary>
    /// Claims the bootstrap singleton for an allowlisted directory identity.
    /// The allowlist is checked before the database is touched, and the winner
    /// is decided by the database, not by this check: a loser re-reads the
    /// external identity so a repeated first sign-in still resolves, and every
    /// other identity fails closed.
    /// </summary>
    private async ValueTask<Result<ExternalIdentityLookupResult>> ProvisionFirstOwnerAsync(
        PlatformOidcProvider provider,
        OidcIdentity identity,
        Guid objectId,
        string issuer,
        string subject,
        CancellationToken cancellationToken)
    {
        if (!_firstOwner.Allows(provider.ProviderId, identity.DirectoryTenantId, objectId))
        {
            return Fail<ExternalIdentityLookupResult>(
                provider.ProviderId,
                CallbackStage,
                "identity_not_a_member_and_not_allowlisted",
                OidcErrors.ProviderRejected,
                identity.DirectoryTenantId,
                subject);
        }

        if (await _provisioning.IsProvisionedAsync(cancellationToken))
        {
            return Fail<ExternalIdentityLookupResult>(
                provider.ProviderId,
                CallbackStage,
                "bootstrap_already_closed",
                OidcErrors.ProviderRejected,
                identity.DirectoryTenantId,
                subject);
        }

        Result<FirstOwnerProvisioningRequest> request = BuildProvisioningRequest(
            provider,
            identity,
            objectId,
            issuer);
        if (!request.TryGetValue(out FirstOwnerProvisioningRequest? provisioning))
        {
            return Fail<ExternalIdentityLookupResult>(
                provider.ProviderId,
                CallbackStage,
                "directory_profile_unusable",
                request.Error!,
                identity.DirectoryTenantId,
                subject);
        }

        FirstOwnerProvisioningStatus status = await _provisioning.ProvisionAsync(
            provisioning,
            beforeCommit: null,
            cancellationToken);
        if (status == FirstOwnerProvisioningStatus.Provisioned)
        {
            _audit.Record(new OidcAuditEvent(
                provider.ProviderId,
                CallbackStage,
                "first_owner_provisioned",
                identity.DirectoryTenantId,
                subject,
                provisioning.Tenant.Id.Value,
                provisioning.User.Id.Value));
        }

        // A concurrent attempt may have won, and it may have been this same
        // identity signing in twice. Only an external identity that actually
        // exists can continue; anything else fails closed.
        ExternalIdentityLookupResult? resolved =
            await _catalog.FindByExternalIdentityAsync(issuer, subject, cancellationToken);
        if (resolved is null)
        {
            return Fail<ExternalIdentityLookupResult>(
                provider.ProviderId,
                CallbackStage,
                status == FirstOwnerProvisioningStatus.Provisioned
                    ? "first_owner_not_readable_after_commit"
                    : "first_owner_bootstrap_lost_to_a_concurrent_attempt",
                OidcErrors.ProviderRejected,
                identity.DirectoryTenantId,
                subject);
        }

        return Result.Success(resolved);
    }

    private Result<FirstOwnerProvisioningRequest> BuildProvisioningRequest(
        PlatformOidcProvider provider,
        OidcIdentity identity,
        Guid objectId,
        string issuer)
    {
        Result<Tenant> tenantResult =
            _tenants.Create(_firstOwner.TenantSlug, _firstOwner.TenantName);
        if (!tenantResult.TryGetValue(out Tenant? tenant))
        {
            return Result.Failure<FirstOwnerProvisioningRequest>(tenantResult.Error!);
        }

        // Email and display name are profile attributes copied onto the owner
        // record; neither took part in the decision to create it.
        Result<User> userResult = _identities.CreateUser(
            identity.Email ?? string.Empty,
            DisplayNameFor(identity));
        if (!userResult.TryGetValue(out User? user))
        {
            return Result.Failure<FirstOwnerProvisioningRequest>(userResult.Error!);
        }

        Result<TenantMembership> membershipResult = _tenants.InviteMember(
            tenant.Id,
            user.Id,
            TenantRole.TenantOwner);
        if (!membershipResult.TryGetValue(out TenantMembership? membership))
        {
            return Result.Failure<FirstOwnerProvisioningRequest>(membershipResult.Error!);
        }

        DateTimeOffset now = _clock.UtcNow.ToUniversalTime();
        Result activated = membership.Activate(now);
        if (activated.IsFailure)
        {
            return Result.Failure<FirstOwnerProvisioningRequest>(activated.Error!);
        }

        Result<AuditChangeSummary> after = AuditChangeSummary.Create(
        [
            AuditField.Plain("tenantSlug", tenant.Slug.Value),
            AuditField.Plain("role", TenantRole.TenantOwner.ToString()),
            AuditField.Plain("identityProvider", provider.ProviderId),
            AuditField.Plain("directoryTenantId", identity.DirectoryTenantId.ToString("D")),
            AuditField.Plain("directoryObjectId", objectId.ToString("D")),
        ]);
        var audit = new AuditRecord(
            new AuditEventId(_ids.NewId()),
            new AuditTenantId(tenant.Id.Value),
            new AuditActor(AuditActorKind.System, "oidc-first-owner-provisioning"),
            "tenant.owner.provisioned",
            new AuditResource("tenant", tenant.Id.Value.ToString("D")),
            AuditChangeSummary.Empty,
            after.TryGetValue(out AuditChangeSummary? summary)
                ? summary
                : AuditChangeSummary.Empty,
            AuditOutcome.Succeeded,
            now);
        return Result.Success(new FirstOwnerProvisioningRequest(
            tenant,
            user,
            membership,
            new ExternalFirstOwnerCredential(
                _ids.NewId(),
                provider.ProviderId,
                issuer,
                identity.DirectoryTenantId,
                objectId),
            audit));
    }

    /// <summary>
    /// Issues the existing Vistara browser session for a resolved principal.
    /// The session is built through a manager bound to one explicit tenant,
    /// because sign-in runs with no ambient tenant scope and the browser may
    /// arrive holding a cookie for another tenant entirely.
    /// </summary>
    private async ValueTask<Result<string>> IssueSessionAsync(
        PlatformOidcProvider provider,
        Guid userId,
        string? existingSessionToken,
        Guid directoryTenantId,
        string subject,
        CancellationToken cancellationToken)
    {
        User? user = await _catalog.FindUserAsync(userId, cancellationToken);
        if (user is null || user.Status != UserStatus.Active)
        {
            return Fail<string>(
                provider.ProviderId,
                CallbackStage,
                "user_disabled",
                OidcErrors.ProviderRejected,
                directoryTenantId,
                subject);
        }

        IReadOnlyList<PersistedTenantMembership> memberships =
            await _catalog.ListMembershipsAsync(userId, cancellationToken);
        PersistedTenantMembership? selected = memberships.FirstOrDefault(candidate =>
            string.Equals(
                candidate.MembershipStatus,
                nameof(MembershipStatus.Active),
                StringComparison.Ordinal) &&
            string.Equals(
                candidate.TenantStatus,
                nameof(TenantStatus.Active),
                StringComparison.Ordinal));
        if (selected is null)
        {
            return Fail<string>(
                provider.ProviderId,
                CallbackStage,
                "no_active_tenant_membership",
                OidcErrors.ProviderRejected,
                directoryTenantId,
                subject);
        }

        // Any session the browser already held is revoked before a new one is
        // issued, so a hosted sign-in cannot be used to keep a prior session
        // alive alongside it.
        _ = await _sessions.LogoutAsync(existingSessionToken, cancellationToken);

        await using TenantScopedSessions scoped = _sessionFactory.Create(selected.TenantId);
        TenantMembership? membership = await scoped.Memberships.FindAsync(
            new TenantId(selected.TenantId),
            user.Id,
            cancellationToken);
        if (membership is null || membership.Status != MembershipStatus.Active)
        {
            return Fail<string>(
                provider.ProviderId,
                CallbackStage,
                "no_active_tenant_membership",
                OidcErrors.ProviderRejected,
                directoryTenantId,
                subject);
        }

        IssuedBrowserSession issued = await scoped.Sessions.IssueAsync(
            user,
            membership,
            existingSessionToken: null,
            cancellationToken);
        _audit.Record(new OidcAuditEvent(
            provider.ProviderId,
            CallbackStage,
            "session_issued",
            directoryTenantId,
            subject,
            selected.TenantId,
            userId));
        return Result.Success(issued.Cookie.ToSetCookieHeader());
    }

    private static string DisplayNameFor(OidcIdentity identity)
    {
        string? name = identity.DisplayName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            return name.Length > 200 ? name[..200] : name;
        }

        string email = identity.Email ?? string.Empty;
        int separator = email.IndexOf('@', StringComparison.Ordinal);
        return separator > 0 ? email[..separator] : email;
    }

    private static bool TryCreateRedemption(
        string? code,
        string codeVerifier,
        out OidcAuthorizationCodeRedemption? redemption)
    {
        redemption = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            redemption = new OidcAuthorizationCodeRedemption(code, codeVerifier);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string CreateBrowserHandle()
    {
        byte[] material = new byte[32];
        try
        {
            _randomSource.Fill(material);
            return Convert.ToBase64String(material)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] Digest(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private Result<OidcHandlePayload> Reject(string providerId, string detail)
    {
        _audit.Record(new OidcAuditEvent(providerId, CallbackStage, detail));
        return Result.Failure<OidcHandlePayload>(OidcErrors.InvalidState);
    }

    private Result<T> Fail<T>(
        string providerId,
        string stage,
        string detail,
        ResultError error,
        Guid? directoryTenantId = null,
        string? objectId = null)
        where T : notnull
    {
        _audit.Record(new OidcAuditEvent(
            providerId,
            stage,
            detail,
            directoryTenantId,
            objectId));
        return Result.Failure<T>(error);
    }
}
