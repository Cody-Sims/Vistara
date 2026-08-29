using Vistara.Application.Common;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Xunit;

namespace Vistara.IntegrationTests.Auth.Cookies;

public sealed class CookieAuthSessionTests
{
    private static readonly DateTimeOffset Now =
        new(2034, 5, 6, 7, 8, 9, TimeSpan.Zero);

    private static readonly UserId UserId =
        new(Guid.Parse("0198b892-ae80-7000-8000-000000000001"));

    private static readonly TenantId TenantOne =
        new(Guid.Parse("0198b892-ae80-7000-8000-000000000002"));

    private static readonly TenantId TenantTwo =
        new(Guid.Parse("0198b892-ae80-7000-8000-000000000003"));

    [Fact]
    public async Task CookieAuth_login_rotates_fixated_session_and_stores_only_digests()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession fixated = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        var credentials = new FakeLocalCredentialVerifier(context.User);
        var handler = new LocalLoginHandler(credentials, context.Manager);

        Result<IssuedBrowserSession> result = await handler.HandleAsync(
            new LocalLoginRequest(
                "alice",
                "correct horse battery staple",
                TenantOne,
                fixated.Cookie.Value),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out IssuedBrowserSession? issued));
        Assert.NotEqual(fixated.Cookie.Value, issued.Cookie.Value);
        Assert.True(context.Store.IsRevoked(fixated.Cookie.Value));
        CookieSessionRecord stored = Assert.Single(context.Store.ActiveRecords);
        Assert.Equal(UserId, stored.UserId);
        Assert.Equal(TenantOne, stored.TenantId);
        Assert.DoesNotContain(issued.Cookie.Value, context.Store.CapturedText(), StringComparison.Ordinal);
        Assert.DoesNotContain(issued.AntiforgeryToken, context.Store.CapturedText(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            issued.Cookie.Value,
            string.Join('|', context.Audit.Events),
            StringComparison.Ordinal);
        Assert.Equal(
            CookieTokenCryptography.ComputeDigest(issued.Cookie.Value),
            stored.SessionTokenDigest);
        Assert.Equal(
            CookieTokenCryptography.ComputeDigest(issued.AntiforgeryToken),
            stored.AntiforgeryTokenDigest);
    }

    [Fact]
    public async Task CookieAuth_login_uses_uniform_redacted_failure_and_honors_cancellation()
    {
        TestContext context = CreateContext();
        var verifier = new FakeLocalCredentialVerifier(null);
        var rejected = new LocalLoginHandler(verifier, context.Manager);
        const string secret = "not-the-password";

        Result<IssuedBrowserSession> failure = await rejected.HandleAsync(
            new LocalLoginRequest("missing@example.test", secret, null, null),
            CancellationToken.None);

        Assert.Equal(CookieAuthErrors.InvalidCredentials.Code, failure.Error?.Code);
        Assert.DoesNotContain("missing", failure.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.Error?.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, string.Join('|', context.Audit.Events), StringComparison.Ordinal);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await rejected.HandleAsync(
                new LocalLoginRequest("alice", secret, null, null),
                cancellation.Token));
        Assert.Equal(1, verifier.VerifyCalls);
    }

    [Fact]
    public async Task CookieAuth_login_and_tenant_switch_select_only_active_memberships()
    {
        TestContext context = CreateContext();
        context.MembershipTwo.Suspend(Now.AddMinutes(1));
        var handler = new LocalLoginHandler(
            new FakeLocalCredentialVerifier(context.User),
            context.Manager);

        Result<IssuedBrowserSession> inactive = await handler.HandleAsync(
            new LocalLoginRequest("alice", "password", TenantTwo, null),
            CancellationToken.None);
        Result<IssuedBrowserSession> active = await handler.HandleAsync(
            new LocalLoginRequest("alice", "password", TenantOne, null),
            CancellationToken.None);

        Assert.Equal(CookieAuthErrors.TenantUnavailable.Code, inactive.Error?.Code);
        Assert.True(active.TryGetValue(out IssuedBrowserSession? issued));

        Result<IssuedBrowserSession> switchResult = await context.TenantSwitcher.SwitchAsync(
            issued.Cookie.Value,
            TenantTwo,
            CancellationToken.None);

        Assert.Equal(CookieAuthErrors.TenantUnavailable.Code, switchResult.Error?.Code);
        Assert.False(context.Store.IsRevoked(issued.Cookie.Value));
    }

    [Fact]
    public async Task CookieAuth_authentication_enforces_idle_absolute_revocation_and_logout()
    {
        TestContext context = CreateContext(
            new CookieAuthOptions(
                TimeSpan.FromMinutes(20),
                TimeSpan.FromHours(2),
                TimeSpan.FromMinutes(10)));
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);

        Result<AuthenticatedBrowserSession> active =
            await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None);
        Assert.True(active.IsSuccess);

        context.Clock.UtcNow = Now.AddMinutes(21);
        Result<AuthenticatedBrowserSession> expired =
            await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None);
        Assert.Equal(CookieAuthErrors.InvalidSession.Code, expired.Error?.Code);

        context.Clock.UtcNow = Now;
        IssuedBrowserSession second = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        BrowserCookie deletion = await context.Logout.LogoutAsync(
            second.Cookie.Value,
            CancellationToken.None);

        Assert.True(deletion.IsDeletion);
        Assert.Equal(TimeSpan.Zero, deletion.MaxAge);
        Assert.True(context.Store.IsRevoked(second.Cookie.Value));
        Assert.Equal(
            CookieAuthErrors.InvalidSession.Code,
            (await context.Authenticator.AuthenticateAsync(
                second.Cookie.Value,
                CancellationToken.None)).Error?.Code);
    }

    [Fact]
    public async Task CookieAuth_absolute_expiry_cannot_be_extended_by_activity()
    {
        TestContext context = CreateContext(
            new CookieAuthOptions(
                TimeSpan.FromMinutes(45),
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(10)));
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        context.Clock.UtcNow = Now.AddMinutes(11);
        Result<AuthenticatedBrowserSession> refreshed =
            await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None);
        Assert.True(refreshed.TryGetValue(out AuthenticatedBrowserSession? active));
        Assert.NotNull(active.RefreshedCookie);
        context.Clock.UtcNow = Now.AddHours(1);

        Result<AuthenticatedBrowserSession> expired =
            await context.Authenticator.AuthenticateAsync(
                active.RefreshedCookie.Value,
                CancellationToken.None);

        Assert.Equal(CookieAuthErrors.InvalidSession.Code, expired.Error?.Code);
    }

    [Fact]
    public async Task CookieAuth_sliding_refresh_rotates_token_without_extending_absolute_expiry()
    {
        TestContext context = CreateContext(
            new CookieAuthOptions(
                TimeSpan.FromMinutes(30),
                TimeSpan.FromHours(1),
                TimeSpan.FromMinutes(10)));
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        DateTimeOffset absoluteExpiry = Assert.Single(context.Store.ActiveRecords).AbsoluteExpiresAt;
        context.Clock.UtcNow = Now.AddMinutes(11);

        Result<AuthenticatedBrowserSession> result =
            await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None);

        Assert.True(result.TryGetValue(out AuthenticatedBrowserSession? authenticated));
        Assert.NotNull(authenticated.RefreshedCookie);
        Assert.NotEqual(issued.Cookie.Value, authenticated.RefreshedCookie.Value);
        Assert.True(context.Store.IsRevoked(issued.Cookie.Value));
        Assert.Equal(
            absoluteExpiry,
            Assert.Single(context.Store.ActiveRecords).AbsoluteExpiresAt);
    }

    [Fact]
    public async Task CookieAuth_membership_or_user_privilege_change_rotates_session()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        Assert.True(context.MembershipOne.ChangeRole(
            TenantRole.TenantAdmin,
            Now.AddMinutes(1)).IsSuccess);
        context.Clock.UtcNow = Now.AddMinutes(1);

        Result<AuthenticatedBrowserSession> roleChanged =
            await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None);

        Assert.True(roleChanged.TryGetValue(out AuthenticatedBrowserSession? authenticated));
        Assert.Equal(TenantRole.TenantAdmin, authenticated.Principal.Role);
        Assert.NotNull(authenticated.RefreshedCookie);
        Assert.True(context.Store.IsRevoked(issued.Cookie.Value));

        Assert.True(context.User.Suspend(Now.AddMinutes(2)).IsSuccess);
        context.Clock.UtcNow = Now.AddMinutes(2);
        Result<AuthenticatedBrowserSession> suspended =
            await context.Authenticator.AuthenticateAsync(
                authenticated.RefreshedCookie.Value,
                CancellationToken.None);

        Assert.Equal(CookieAuthErrors.InvalidSession.Code, suspended.Error?.Code);
        Assert.True(context.Store.IsRevoked(authenticated.RefreshedCookie.Value));
    }

    [Fact]
    public async Task CookieAuth_tenant_switch_rotates_session_and_uses_latest_membership()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);

        Result<IssuedBrowserSession> result = await context.TenantSwitcher.SwitchAsync(
            issued.Cookie.Value,
            TenantTwo,
            CancellationToken.None);

        Assert.True(result.TryGetValue(out IssuedBrowserSession? switched));
        Assert.Equal(TenantTwo, switched.Principal.TenantId);
        Assert.Equal(TenantRole.Viewer, switched.Principal.Role);
        Assert.NotEqual(issued.Cookie.Value, switched.Cookie.Value);
        Assert.True(context.Store.IsRevoked(issued.Cookie.Value));
    }

    [Fact]
    public async Task CookieAuth_concurrent_privilege_rotation_rejects_replay()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        Assert.True(context.MembershipOne.ChangeRole(
            TenantRole.TenantAdmin,
            Now.AddMinutes(1)).IsSuccess);
        context.Clock.UtcNow = Now.AddMinutes(1);
        context.Store.PauseNextRotation();

        Task<Result<AuthenticatedBrowserSession>> first =
            context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None).AsTask();
        Task<Result<AuthenticatedBrowserSession>> second =
            context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                CancellationToken.None).AsTask();
        context.Store.ReleaseRotation();
        Result<AuthenticatedBrowserSession>[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.Error?.Code == CookieAuthErrors.InvalidSession.Code);
        Assert.Single(context.Store.ActiveRecords);
    }

    [Fact]
    public async Task CookieAuth_session_invalidation_revokes_user_and_membership_sessions()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession tenantOne = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        IssuedBrowserSession tenantTwo = await context.Manager.IssueAsync(
            context.User,
            context.MembershipTwo,
            null,
            CancellationToken.None);

        await context.Invalidator.InvalidateMembershipAsync(
            UserId,
            TenantOne,
            CancellationToken.None);

        Assert.True(context.Store.IsRevoked(tenantOne.Cookie.Value));
        Assert.False(context.Store.IsRevoked(tenantTwo.Cookie.Value));

        await context.Invalidator.InvalidateUserAsync(UserId, CancellationToken.None);

        Assert.True(context.Store.IsRevoked(tenantTwo.Cookie.Value));
    }

    [Fact]
    public async Task CookieAuth_external_oidc_hook_is_provider_neutral_and_rotates_existing_session()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession existing = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        var linker = new FakeExternalIdentityLinker(context.User);
        var handler = new ExternalOidcLoginHandler(linker, context.Manager);
        var external = new ExternalOidcLoginResult(
            "https://identity.example",
            "provider-subject",
            "alice@example.test",
            "Alice");

        Result<IssuedBrowserSession> result = await handler.HandleAsync(
            external,
            TenantOne,
            existing.Cookie.Value,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Same(external, linker.LastResult);
        Assert.True(context.Store.IsRevoked(existing.Cookie.Value));
    }

    [Fact]
    public async Task CookieAuth_malformed_session_is_rejected_without_lookup_or_identifier_disclosure()
    {
        TestContext context = CreateContext();
        string malformed = new('x', 200);

        Result<AuthenticatedBrowserSession> result =
            await context.Authenticator.AuthenticateAsync(
                malformed,
                CancellationToken.None);

        Assert.Equal(CookieAuthErrors.InvalidSession.Code, result.Error?.Code);
        Assert.Equal(0, context.Store.FindCalls);
        Assert.DoesNotContain(
            malformed,
            result.Error?.Message ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            malformed,
            string.Join('|', context.Audit.Events),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CookieAuth_security_sensitive_operations_honor_pre_cancellation()
    {
        TestContext context = CreateContext();
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);
        int findCalls = context.Store.FindCalls;
        int revokeCalls = context.Store.RevokeCalls;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.Authenticator.AuthenticateAsync(
                issued.Cookie.Value,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.Logout.LogoutAsync(
                issued.Cookie.Value,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.TenantSwitcher.SwitchAsync(
                issued.Cookie.Value,
                TenantTwo,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.Invalidator.InvalidateUserAsync(
                UserId,
                cancellation.Token));

        Assert.Equal(findCalls, context.Store.FindCalls);
        Assert.Equal(revokeCalls, context.Store.RevokeCalls);
        Assert.False(context.Store.IsRevoked(issued.Cookie.Value));
    }

    [Fact]
    public void CookieAuth_cryptographic_token_source_returns_full_non_repeating_entropy()
    {
        var source = new CryptographicCookieTokenSource();
        byte[] first = new byte[CookieTokenFormatForTests.ByteLength];
        byte[] second = new byte[CookieTokenFormatForTests.ByteLength];

        source.Fill(first);
        source.Fill(second);

        Assert.Contains(first, value => value != 0);
        Assert.Contains(second, value => value != 0);
        Assert.False(first.SequenceEqual(second));
    }

    [Fact]
    public async Task CookieAuth_sensitive_contracts_redact_string_representations()
    {
        TestContext context = CreateContext();
        var request = new LocalLoginRequest(
            "alice",
            "highly-secret-password",
            TenantOne,
            "existing-session-token");
        IssuedBrowserSession issued = await context.Manager.IssueAsync(
            context.User,
            context.MembershipOne,
            null,
            CancellationToken.None);

        Assert.DoesNotContain(
            request.Password,
            request.ToString(),
            StringComparison.Ordinal);
        string existingSessionToken = Assert.IsType<string>(
            request.ExistingSessionToken);
        Assert.DoesNotContain(
            existingSessionToken,
            request.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            issued.Cookie.Value,
            issued.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            issued.AntiforgeryToken,
            issued.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            issued.Cookie.Value,
            issued.Cookie.ToString(),
            StringComparison.Ordinal);
    }

    private static TestContext CreateContext(CookieAuthOptions? options = null)
    {
        User user = CreateUser();
        TenantMembership membershipOne = CreateMembership(
            TenantOne,
            TenantRole.Member);
        TenantMembership membershipTwo = CreateMembership(
            TenantTwo,
            TenantRole.Viewer);
        var clock = new MutableClock(Now);
        var users = new FakeUserRepository(user);
        var memberships = new FakeMembershipRepository(
            membershipOne,
            membershipTwo);
        var store = new FakeCookieSessionStore();
        var audit = new FakeCookieAuthAuditSink();
        var manager = new CookieSessionManager(
            clock,
            new SequenceUuid7Generator(Now),
            new SequenceCookieTokenSource(),
            users,
            memberships,
            store,
            options ?? new CookieAuthOptions(),
            audit);
        return new TestContext(
            user,
            membershipOne,
            membershipTwo,
            clock,
            store,
            audit,
            manager,
            new CookieAuthenticationHandler(manager),
            new CookieLogoutHandler(manager),
            new CookieTenantSwitcher(manager),
            new CookieSessionInvalidator(store, clock));
    }

    private static User CreateUser()
    {
        Result<User> result = User.Create(
            UserId,
            "alice@example.test",
            "Alice",
            Now);
        Assert.True(result.TryGetValue(out User? user));
        Assert.True(user.LinkLocalIdentity(
            new LocalIdentityId(Guid.Parse("0198b892-ae80-7000-8000-000000000004")),
            "alice",
            Now).IsSuccess);
        return user;
    }

    private static TenantMembership CreateMembership(
        TenantId tenantId,
        TenantRole role)
    {
        Result<TenantMembership> result = TenantMembership.Invite(
            tenantId,
            UserId,
            role,
            Now);
        Assert.True(result.TryGetValue(out TenantMembership? membership));
        Assert.True(membership.Activate(Now).IsSuccess);
        return membership;
    }

    private sealed record TestContext(
        User User,
        TenantMembership MembershipOne,
        TenantMembership MembershipTwo,
        MutableClock Clock,
        FakeCookieSessionStore Store,
        FakeCookieAuthAuditSink Audit,
        CookieSessionManager Manager,
        CookieAuthenticationHandler Authenticator,
        CookieLogoutHandler Logout,
        CookieTenantSwitcher TenantSwitcher,
        CookieSessionInvalidator Invalidator);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class SequenceUuid7Generator(DateTimeOffset timestamp) : IUuid7Generator
    {
        private int _sequence;

        public Guid NewId() =>
            Guid.CreateVersion7(timestamp.AddMilliseconds(Interlocked.Increment(ref _sequence)));
    }

    private sealed class SequenceCookieTokenSource : ICookieTokenSource
    {
        private int _value;

        public void Fill(Span<byte> destination) =>
            destination.Fill(checked((byte)Interlocked.Increment(ref _value)));
    }

    private sealed class FakeLocalCredentialVerifier(User? user) : ILocalCredentialVerifier
    {
        public int VerifyCalls { get; private set; }

        public ValueTask<User?> VerifyAsync(
            string login,
            string password,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCalls++;
            return ValueTask.FromResult(user);
        }
    }

    private sealed class FakeExternalIdentityLinker(User? user) : IExternalIdentityLinker
    {
        public ExternalOidcLoginResult? LastResult { get; private set; }

        public ValueTask<User?> ResolveOrLinkAsync(
            ExternalOidcLoginResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastResult = result;
            return ValueTask.FromResult(user);
        }
    }

    private sealed class FakeUserRepository(User user) : IUserRepository
    {
        public ValueTask<User?> FindByIdAsync(
            UserId id,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<User?>(id == user.Id ? user : null);
        }

        public ValueTask<User?> FindByEmailAsync(
            NormalizedEmail email,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<User?>(null);

        public ValueTask<User?> FindByLocalIdentityAsync(
            NormalizedLogin login,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<User?>(null);

        public ValueTask<User?> FindByExternalIdentityAsync(
            ExternalIssuer issuer,
            string subject,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<User?>(null);

        public ValueTask AddAsync(User added, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            User updated,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeMembershipRepository(
        params TenantMembership[] memberships) : ITenantMembershipRepository
    {
        public ValueTask<TenantMembership?> FindAsync(
            TenantId tenantId,
            UserId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                memberships.SingleOrDefault(
                    membership =>
                        membership.TenantId == tenantId &&
                        membership.UserId == userId));
        }

        public ValueTask<IReadOnlyList<TenantMembership>> ListForUserAsync(
            UserId userId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TenantMembership> result = memberships
                .Where(membership => membership.UserId == userId)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask AddAsync(
            TenantMembership membership,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdateAsync(
            TenantMembership membership,
            long expectedVersion,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeCookieAuthAuditSink : ICookieAuthAuditSink
    {
        public List<CookieAuthAuditEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            CookieAuthAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCookieSessionStore : ICookieSessionStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CookieSessionRecord> _sessions =
            new(StringComparer.Ordinal);
        private TaskCompletionSource? _rotationEntered;
        private TaskCompletionSource? _rotationRelease;

        public int FindCalls { get; private set; }

        public int RevokeCalls { get; private set; }

        public IReadOnlyList<CookieSessionRecord> ActiveRecords
        {
            get
            {
                lock (_gate)
                {
                    return _sessions.Values
                        .Where(record => record.RevokedAt is null)
                        .ToArray();
                }
            }
        }

        public ValueTask<CookieSessionRecord?> FindAsync(
            string sessionTokenDigest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                FindCalls++;
                _sessions.TryGetValue(sessionTokenDigest, out CookieSessionRecord? record);
                return ValueTask.FromResult(record);
            }
        }

        public ValueTask<bool> AddAsync(
            CookieSessionRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return ValueTask.FromResult(
                    _sessions.TryAdd(record.SessionTokenDigest, record));
            }
        }

        public async ValueTask<bool> RotateAsync(
            string currentSessionTokenDigest,
            long expectedVersion,
            CookieSessionRecord replacement,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource? entered = _rotationEntered;
            TaskCompletionSource? release = _rotationRelease;
            entered?.TrySetResult();
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                if (!_sessions.TryGetValue(
                        currentSessionTokenDigest,
                        out CookieSessionRecord? current) ||
                    current.Version != expectedVersion ||
                    current.RevokedAt is not null)
                {
                    return false;
                }

                _sessions[currentSessionTokenDigest] = current with
                {
                    RevokedAt = revokedAt,
                    Version = current.Version + 1,
                };
                return _sessions.TryAdd(
                    replacement.SessionTokenDigest,
                    replacement);
            }
        }

        public ValueTask RevokeAsync(
            string sessionTokenDigest,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                RevokeCalls++;
                if (_sessions.TryGetValue(
                        sessionTokenDigest,
                        out CookieSessionRecord? current) &&
                    current.RevokedAt is null)
                {
                    _sessions[sessionTokenDigest] = current with
                    {
                        RevokedAt = revokedAt,
                        Version = current.Version + 1,
                    };
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeUserAsync(
            UserId userId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokeWhere(record => record.UserId == userId, revokedAt);
            return ValueTask.CompletedTask;
        }

        public ValueTask RevokeMembershipAsync(
            UserId userId,
            TenantId tenantId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokeWhere(
                record => record.UserId == userId && record.TenantId == tenantId,
                revokedAt);
            return ValueTask.CompletedTask;
        }

        public bool IsRevoked(string plaintextToken)
        {
            string digest = CookieTokenCryptography.ComputeDigest(plaintextToken);
            lock (_gate)
            {
                return _sessions.TryGetValue(digest, out CookieSessionRecord? record) &&
                    record.RevokedAt is not null;
            }
        }

        public string CapturedText()
        {
            lock (_gate)
            {
                return string.Join('|', _sessions.Values);
            }
        }

        public void PauseNextRotation()
        {
            _rotationEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _rotationRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseRotation()
        {
            _rotationEntered?.Task.GetAwaiter().GetResult();
            _rotationRelease?.TrySetResult();
            _rotationEntered = null;
            _rotationRelease = null;
        }

        private void RevokeWhere(
            Func<CookieSessionRecord, bool> predicate,
            DateTimeOffset revokedAt)
        {
            lock (_gate)
            {
                foreach ((string digest, CookieSessionRecord record) in
                         _sessions.Where(pair => predicate(pair.Value)).ToArray())
                {
                    _sessions[digest] = record with
                    {
                        RevokedAt = revokedAt,
                        Version = record.Version + 1,
                    };
                }
            }
        }
    }

    private static class CookieTokenFormatForTests
    {
        public const int ByteLength = 32;
    }
}
