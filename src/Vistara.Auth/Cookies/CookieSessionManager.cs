using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.Cookies;

public sealed class CookieSessionManager
{
    private const int MaximumTokenGenerationAttempts = 3;
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly ICookieTokenSource _tokenSource;
    private readonly IUserRepository _users;
    private readonly ITenantMembershipRepository _memberships;
    private readonly ICookieSessionStore _store;
    private readonly CookieAuthOptions _options;
    private readonly ICookieAuthAuditSink _audit;

    public CookieSessionManager(
        IClock clock,
        IUuid7Generator idGenerator,
        ICookieTokenSource tokenSource,
        IUserRepository users,
        ITenantMembershipRepository memberships,
        ICookieSessionStore store,
        CookieAuthOptions options,
        ICookieAuthAuditSink audit)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _users = users ?? throw new ArgumentNullException(nameof(users));
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async ValueTask<IssuedBrowserSession> IssueAsync(
        User user,
        TenantMembership? membership,
        string? existingSessionToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIssuance(user, membership);
        DateTimeOffset now = _clock.UtcNow;
        string antiforgeryToken = CookieTokenFormat.Create(_tokenSource);

        for (int attempt = 0; attempt < MaximumTokenGenerationAttempts; attempt++)
        {
            string sessionToken = CookieTokenFormat.Create(_tokenSource);
            CookieSessionRecord record = CreateInitialRecord(
                sessionToken,
                antiforgeryToken,
                user,
                membership,
                now);
            bool stored = await StoreInitialAsync(
                existingSessionToken,
                record,
                now,
                cancellationToken);
            if (!stored)
            {
                continue;
            }

            var principal = CreatePrincipal(record);
            await CookieAuthTelemetry.TryWriteAsync(
                _audit,
                new CookieAuthAuditEvent(
                    CookieAuthAuditAction.LoginSucceeded,
                    user.Id,
                    membership?.TenantId,
                    null,
                    now));
            return new IssuedBrowserSession(
                principal,
                CreateCookie(sessionToken, record, now),
                antiforgeryToken);
        }

        throw new InvalidOperationException("A unique browser session token could not be created.");
    }

    public async ValueTask<Result<AuthenticatedBrowserSession>> AuthenticateAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        SessionResolution? resolution = await ResolveAsync(
            sessionToken,
            now,
            cancellationToken);
        if (resolution is null)
        {
            return await RejectSessionAsync(now);
        }

        bool privilegeChanged =
            resolution.Record.UserVersion != resolution.User.Version ||
            resolution.Record.MembershipVersion != resolution.Membership?.Version ||
            resolution.Record.Role != resolution.Membership?.Role;
        bool slidingRefresh =
            now - resolution.Record.LastSeenAt >= _options.SlidingRefreshInterval;

        BrowserCookie? refreshedCookie = null;
        CookieSessionRecord effectiveRecord = resolution.Record;
        if (privilegeChanged || slidingRefresh)
        {
            string replacementToken = CookieTokenFormat.Create(_tokenSource);
            CookieSessionRecord replacement = CreateReplacementRecord(
                resolution.Record,
                replacementToken,
                resolution.User,
                resolution.Membership,
                resolution.Record.AntiforgeryTokenDigest,
                now);
            bool rotated = await _store.RotateAsync(
                resolution.Record.SessionTokenDigest,
                resolution.Record.Version,
                replacement,
                now,
                cancellationToken);
            if (!rotated)
            {
                return await RejectSessionAsync(now);
            }

            effectiveRecord = replacement;
            refreshedCookie = CreateCookie(replacementToken, replacement, now);
            await CookieAuthTelemetry.TryWriteAsync(
                _audit,
                new CookieAuthAuditEvent(
                    CookieAuthAuditAction.SessionRotated,
                    replacement.UserId,
                    replacement.TenantId,
                    privilegeChanged ? "privilege_changed" : "sliding_refresh",
                    now));
        }

        await CookieAuthTelemetry.TryWriteAsync(
            _audit,
            new CookieAuthAuditEvent(
                CookieAuthAuditAction.SessionAuthenticated,
                effectiveRecord.UserId,
                effectiveRecord.TenantId,
                null,
                now));
        return Result.Success(
            new AuthenticatedBrowserSession(
                CreatePrincipal(effectiveRecord),
                refreshedCookie));
    }

    public async ValueTask<Result<IssuedBrowserSession>> SwitchTenantAsync(
        string? sessionToken,
        TenantId tenantId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        SessionResolution? resolution = await ResolveAsync(
            sessionToken,
            now,
            cancellationToken);
        if (resolution is null)
        {
            return Result.Failure<IssuedBrowserSession>(CookieAuthErrors.InvalidSession);
        }

        TenantMembership? target = await _memberships.FindAsync(
            tenantId,
            resolution.User.Id,
            cancellationToken);
        if (target?.Status != MembershipStatus.Active)
        {
            return Result.Failure<IssuedBrowserSession>(CookieAuthErrors.TenantUnavailable);
        }

        string replacementToken = CookieTokenFormat.Create(_tokenSource);
        string antiforgeryToken = CookieTokenFormat.Create(_tokenSource);
        CookieSessionRecord replacement = CreateReplacementRecord(
            resolution.Record,
            replacementToken,
            resolution.User,
            target,
            CookieTokenCryptography.ComputeDigest(antiforgeryToken),
            now);
        bool rotated = await _store.RotateAsync(
            resolution.Record.SessionTokenDigest,
            resolution.Record.Version,
            replacement,
            now,
            cancellationToken);
        if (!rotated)
        {
            return Result.Failure<IssuedBrowserSession>(CookieAuthErrors.InvalidSession);
        }

        await CookieAuthTelemetry.TryWriteAsync(
            _audit,
            new CookieAuthAuditEvent(
                CookieAuthAuditAction.SessionRotated,
                resolution.User.Id,
                target.TenantId,
                "tenant_changed",
                now));
        return Result.Success(
            new IssuedBrowserSession(
                CreatePrincipal(replacement),
                CreateCookie(replacementToken, replacement, now),
                antiforgeryToken));
    }

    public async ValueTask<BrowserCookie> LogoutAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        if (CookieTokenFormat.TryComputeDigest(sessionToken, out string digest))
        {
            await _store.RevokeAsync(digest, now, cancellationToken);
        }

        await CookieAuthTelemetry.TryWriteAsync(
            _audit,
            new CookieAuthAuditEvent(
                CookieAuthAuditAction.LoggedOut,
                null,
                null,
                null,
                now));
        return BrowserCookie.Delete(_options);
    }

    internal async ValueTask<Result<TenantSelection>> SelectMembershipAsync(
        UserId userId,
        TenantId? requestedTenantId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantMembership> memberships =
            await _memberships.ListForUserAsync(userId, cancellationToken);
        TenantMembership? selected = requestedTenantId.HasValue
            ? memberships.SingleOrDefault(
                membership =>
                    membership.TenantId == requestedTenantId.Value &&
                    membership.Status == MembershipStatus.Active)
            : memberships
                .Where(membership => membership.Status == MembershipStatus.Active)
                .OrderBy(membership => membership.TenantId.Value)
                .FirstOrDefault();

        return requestedTenantId.HasValue && selected is null
            ? Result.Failure<TenantSelection>(CookieAuthErrors.TenantUnavailable)
            : Result.Success(new TenantSelection(selected));
    }

    internal ValueTask RecordLoginRejectionAsync(DateTimeOffset now) =>
        CookieAuthTelemetry.TryWriteAsync(
            _audit,
            new CookieAuthAuditEvent(
                CookieAuthAuditAction.LoginRejected,
                null,
                null,
                CookieAuthErrors.InvalidCredentials.Code,
                now));

    internal DateTimeOffset UtcNow => _clock.UtcNow;

    private async ValueTask<SessionResolution?> ResolveAsync(
        string? sessionToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!CookieTokenFormat.TryComputeDigest(sessionToken, out string digest))
        {
            return null;
        }

        CookieSessionRecord? record =
            await _store.FindAsync(digest, cancellationToken);
        if (record is null || !record.IsActive(now))
        {
            if (record?.RevokedAt is null)
            {
                await _store.RevokeAsync(digest, now, cancellationToken);
            }

            return null;
        }

        User? user = await _users.FindByIdAsync(record.UserId, cancellationToken);
        if (user?.Status != UserStatus.Active)
        {
            await _store.RevokeAsync(digest, now, cancellationToken);
            return null;
        }

        TenantMembership? membership = null;
        if (record.TenantId.HasValue)
        {
            membership = await _memberships.FindAsync(
                record.TenantId.Value,
                record.UserId,
                cancellationToken);
            if (membership?.Status != MembershipStatus.Active)
            {
                await _store.RevokeAsync(digest, now, cancellationToken);
                return null;
            }
        }

        return new SessionResolution(record, user, membership);
    }

    private async ValueTask<bool> StoreInitialAsync(
        string? existingSessionToken,
        CookieSessionRecord replacement,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!CookieTokenFormat.TryComputeDigest(
                existingSessionToken,
                out string existingDigest))
        {
            return await _store.AddAsync(replacement, cancellationToken);
        }

        CookieSessionRecord? existing =
            await _store.FindAsync(existingDigest, cancellationToken);
        if (existing is null || existing.RevokedAt.HasValue)
        {
            return await _store.AddAsync(replacement, cancellationToken);
        }

        bool rotated = await _store.RotateAsync(
            existingDigest,
            existing.Version,
            replacement,
            now,
            cancellationToken);
        return rotated || await _store.AddAsync(replacement, cancellationToken);
    }

    private CookieSessionRecord CreateInitialRecord(
        string sessionToken,
        string antiforgeryToken,
        User user,
        TenantMembership? membership,
        DateTimeOffset now)
    {
        DateTimeOffset absoluteExpiry = now + _options.AbsoluteLifetime;
        return new CookieSessionRecord(
            new AuthSessionId(_idGenerator.NewId()),
            CookieTokenCryptography.ComputeDigest(sessionToken),
            CookieTokenCryptography.ComputeDigest(antiforgeryToken),
            user.Id,
            membership?.TenantId,
            membership?.Role,
            user.Version,
            membership?.Version,
            now,
            now,
            Minimum(now + _options.IdleLifetime, absoluteExpiry),
            absoluteExpiry);
    }

    private CookieSessionRecord CreateReplacementRecord(
        CookieSessionRecord current,
        string replacementToken,
        User user,
        TenantMembership? membership,
        string antiforgeryDigest,
        DateTimeOffset now) =>
        new(
            new AuthSessionId(_idGenerator.NewId()),
            CookieTokenCryptography.ComputeDigest(replacementToken),
            antiforgeryDigest,
            user.Id,
            membership?.TenantId,
            membership?.Role,
            user.Version,
            membership?.Version,
            current.IssuedAt,
            now,
            Minimum(now + _options.IdleLifetime, current.AbsoluteExpiresAt),
            current.AbsoluteExpiresAt);

    private BrowserCookie CreateCookie(
        string plaintextToken,
        CookieSessionRecord record,
        DateTimeOffset now) =>
        BrowserCookie.Session(
            _options,
            plaintextToken,
            Minimum(record.IdleExpiresAt, record.AbsoluteExpiresAt) - now);

    private static CookieAuthPrincipal CreatePrincipal(CookieSessionRecord record) =>
        new(
            record.UserId,
            record.TenantId,
            record.Role,
            record.AntiforgeryTokenDigest);

    private static DateTimeOffset Minimum(
        DateTimeOffset first,
        DateTimeOffset second) =>
        first <= second ? first : second;

    private static void ValidateIssuance(
        User user,
        TenantMembership? membership)
    {
        if (user.Status != UserStatus.Active)
        {
            throw new InvalidOperationException("Only active users may receive browser sessions.");
        }

        if (membership is not null &&
            (membership.UserId != user.Id ||
             membership.Status != MembershipStatus.Active))
        {
            throw new InvalidOperationException(
                "Only active memberships may be selected for browser sessions.");
        }
    }

    private async ValueTask<Result<AuthenticatedBrowserSession>> RejectSessionAsync(
        DateTimeOffset now)
    {
        await CookieAuthTelemetry.TryWriteAsync(
            _audit,
            new CookieAuthAuditEvent(
                CookieAuthAuditAction.SessionRejected,
                null,
                null,
                CookieAuthErrors.InvalidSession.Code,
                now));
        return Result.Failure<AuthenticatedBrowserSession>(
            CookieAuthErrors.InvalidSession);
    }

    private sealed record SessionResolution(
        CookieSessionRecord Record,
        User User,
        TenantMembership? Membership);

    internal sealed record TenantSelection(TenantMembership? Membership);
}
