using System.Text;
using Vistara.Application.Common;
using Vistara.Auth.ApiKeys;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Xunit;

namespace Vistara.IntegrationTests.Auth.ApiKeys;

public sealed class ApiKeyServicesTests
{
    private static readonly DateTimeOffset Now =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly TenantId TenantId =
        new(Guid.CreateVersion7(Now));

    private static readonly UserId OwnerId =
        new(Guid.CreateVersion7(Now.AddMilliseconds(1)));

    private static readonly ApiKeyId KeyId =
        new(Guid.Parse("0b8e1ca2-306e-7bd3-9479-71439b417baa"));

    [Fact]
    public async Task ApiKeys_issue_returns_plaintext_once_and_persists_only_identifier_and_digest()
    {
        byte[] secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var store = new FakeApiKeyStore();
        var audit = new FakeApiKeyAuditSink();
        ApiKeyIssuer issuer = CreateIssuer(store, audit, secret);

        Result<IssuedApiKey> result = await issuer.IssueAsync(
            new ApiKeyIssueRequest(
                TenantId,
                OwnerId,
                ApiKeyScope.ReadAssets | ApiKeyScope.UploadAssets,
                Now.AddDays(30)),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out IssuedApiKey? issued));
        Assert.Equal(
            $"vst_v1{KeyId.Value:N}_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8",
            issued.PlaintextKey);
        Assert.NotNull(store.Added);
        Assert.Equal($"vst_v1{KeyId.Value:N}", store.Added.Prefix.Value);
        Assert.Equal(64, store.Added.Digest.Value.Length);
        Assert.DoesNotContain(issued.PlaintextKey, store.CapturedText(), StringComparison.Ordinal);
        Assert.Single(audit.Events);
        Assert.Equal(ApiKeyAuditEvent.RedactedPresentedKey, audit.Events[0].PresentedKey);
        Assert.DoesNotContain(issued.PlaintextKey, audit.Events[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKeys_secure_random_source_produces_full_non_repeating_secrets()
    {
        var source = new CryptographicApiKeyRandomSource();
        byte[] first = new byte[ApiKeyFormat.SecretByteLength];
        byte[] second = new byte[ApiKeyFormat.SecretByteLength];

        source.Fill(first);
        source.Fill(second);

        Assert.Contains(first, value => value != 0);
        Assert.Contains(second, value => value != 0);
        Assert.False(first.SequenceEqual(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("vst_v1")]
    [InlineData("vst_v10b8e1ca2306e7bd3947971439b417baa_not-base64!")]
    [InlineData("VST_v10b8e1ca2306e7bd3947971439b417baa_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8")]
    [InlineData("vst_v10b8e1ca2306e7bd3947971439b417baa_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9")]
    public async Task ApiKeys_authentication_rejects_malformed_keys_without_store_lookup(string plaintext)
    {
        var store = new FakeApiKeyStore();
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store);

        Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
            plaintext,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.Equal(ApiKeyErrors.InvalidCredentials.Code, result.Error?.Code);
        Assert.Equal(0, store.FindCalls);
        if (plaintext.Length > 0)
        {
            Assert.DoesNotContain(
                plaintext,
                result.Error?.Message ?? string.Empty,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ApiKeys_authentication_rejects_oversized_input_before_store_lookup()
    {
        string plaintext = new('a', ApiKeyFormat.MaximumPlaintextLength + 1);
        var store = new FakeApiKeyStore();
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store);

        Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
            plaintext,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.Equal(ApiKeyErrors.InvalidCredentials.Code, result.Error?.Code);
        Assert.Equal(0, store.FindCalls);
    }

    [Fact]
    public async Task ApiKeys_authentication_rejects_digest_created_with_another_pepper()
    {
        var store = new FakeApiKeyStore();
        Result<IssuedApiKey> issued = await CreateIssuer(
                store,
                new FakeApiKeyAuditSink(),
                Enumerable.Repeat((byte)42, 32).ToArray())
            .IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
                CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));

        ApiKeyAuthenticator authenticator = CreateAuthenticator(
            store,
            pepper: Enumerable.Repeat((byte)99, 32).ToArray());
        Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
            key.PlaintextKey,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.Equal(ApiKeyErrors.InvalidCredentials.Code, result.Error?.Code);
    }

    [Fact]
    public async Task ApiKeys_authentication_uses_the_timing_safe_digest_comparer()
    {
        byte[] secret = Enumerable.Repeat((byte)42, 32).ToArray();
        var store = new FakeApiKeyStore();
        Result<IssuedApiKey> issued = await CreateIssuer(
                store,
                new FakeApiKeyAuditSink(),
                secret)
            .IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
                CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));
        var comparer = new RecordingDigestComparer();
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store, comparer: comparer);

        Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
            key.PlaintextKey,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(comparer.WasCalled);
        Assert.Equal(32, comparer.ExpectedLength);
        Assert.Equal(32, comparer.ActualLength);
    }

    [Fact]
    public async Task ApiKeys_authentication_checks_expiry_revocation_scopes_and_tenant_status()
    {
        byte[] secret = Enumerable.Repeat((byte)42, 32).ToArray();

        await AssertRejectedAsync(
            secret,
            expiresAt: Now.AddSeconds(1),
            ApiKeyScope.ReadAssets,
            TenantStatus.Active,
            revoke: false,
            ApiKeyErrors.Expired);
        await AssertRejectedAsync(
            secret,
            expiresAt: null,
            ApiKeyScope.ReadAssets,
            TenantStatus.Active,
            revoke: true,
            ApiKeyErrors.Revoked);
        await AssertRejectedAsync(
            secret,
            expiresAt: null,
            ApiKeyScope.ReadAssets,
            TenantStatus.Active,
            revoke: false,
            ApiKeyErrors.InsufficientScope,
            requiredScope: ApiKeyScope.ManageApiKeys);
        await AssertRejectedAsync(
            secret,
            expiresAt: null,
            ApiKeyScope.ReadAssets,
            TenantStatus.Suspended,
            revoke: false,
            ApiKeyErrors.TenantInactive);
    }

    [Fact]
    public async Task ApiKeys_revocation_is_observed_on_the_next_store_backed_authentication()
    {
        byte[] secret = Enumerable.Repeat((byte)42, 32).ToArray();
        var store = new FakeApiKeyStore();
        Result<IssuedApiKey> issued = await CreateIssuer(
                store,
                new FakeApiKeyAuditSink(),
                secret)
            .IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
                CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store);

        Assert.True((await authenticator.AuthenticateAsync(
            key.PlaintextKey,
            ApiKeyScope.ReadAssets,
            CancellationToken.None)).IsSuccess);

        ApiKeyRevoker revoker = new(
            new FakeClock(Now.AddMinutes(1)),
            store,
            new FakeApiKeyAuditSink());
        Assert.True((await revoker.RevokeAsync(
            TenantId,
            OwnerId,
            KeyId,
            CancellationToken.None)).IsSuccess);

        Result<ApiKeyPrincipal> rejected = await authenticator.AuthenticateAsync(
            key.PlaintextKey,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.Equal(ApiKeyErrors.Revoked.Code, rejected.Error?.Code);
        Assert.Equal(2, store.FindCalls);
    }

    [Fact]
    public async Task ApiKeys_authentication_success_does_not_depend_on_usage_or_audit_telemetry()
    {
        byte[] secret = Enumerable.Repeat((byte)42, 32).ToArray();
        var store = new FakeApiKeyStore();
        Result<IssuedApiKey> issued = await CreateIssuer(
                store,
                new FakeApiKeyAuditSink(),
                secret)
            .IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
                CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));
        store.ThrowOnUsage = true;
        var audit = new FakeApiKeyAuditSink { ThrowOnWrite = true };
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store, audit: audit);

        Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
            key.PlaintextKey,
            ApiKeyScope.ReadAssets,
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new DateTimeOffset(2032, 3, 4, 5, 6, 0, TimeSpan.Zero),
            store.LastUsageAttempt);
    }

    [Fact]
    public async Task ApiKeys_operations_honor_cancellation_before_security_sensitive_work()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new FakeApiKeyStore();
        var random = new DeterministicRandomSource(Enumerable.Repeat((byte)42, 32).ToArray());
        ApiKeyIssuer issuer = CreateIssuer(store, new FakeApiKeyAuditSink(), random: random);
        ApiKeyAuthenticator authenticator = CreateAuthenticator(store);
        ApiKeyRevoker revoker = new(new FakeClock(Now), store, new FakeApiKeyAuditSink());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await issuer.IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await authenticator.AuthenticateAsync(
                "anything",
                ApiKeyScope.ReadAssets,
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await revoker.RevokeAsync(TenantId, OwnerId, KeyId, cancellation.Token));

        Assert.False(random.WasCalled);
        Assert.Null(store.Added);
        Assert.Equal(0, store.FindCalls);
        Assert.Equal(0, store.RevokeCalls);
    }

    [Fact]
    public async Task ApiKeys_pepper_version_is_embedded_and_unknown_versions_are_rejected()
    {
        byte[] secret = Enumerable.Repeat((byte)42, 32).ToArray();
        var store = new FakeApiKeyStore();
        var peppers = new ApiKeyPepperSet(
            "v2",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)1, 32).ToArray(),
                ["v2"] = Enumerable.Repeat((byte)2, 32).ToArray(),
            });
        var issuer = new ApiKeyIssuer(
            new FakeClock(Now),
            new FakeUuid7Generator(KeyId.Value),
            new DeterministicRandomSource(secret),
            peppers,
            store,
            new FakeApiKeyAuditSink());
        Result<IssuedApiKey> issued = await issuer.IssueAsync(
            new ApiKeyIssueRequest(TenantId, OwnerId, ApiKeyScope.ReadAssets, null),
            CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));
        Assert.StartsWith("vst_v2", key.PlaintextKey, StringComparison.Ordinal);

        string unknownVersionKey = key.PlaintextKey.Replace("vst_v2", "vst_v9", StringComparison.Ordinal);
        Result<ApiKeyPrincipal> result = await new ApiKeyAuthenticator(
                new FakeClock(Now),
                peppers,
                store,
                new FixedTimeApiKeyDigestComparer(),
                new FakeApiKeyAuditSink())
            .AuthenticateAsync(
                unknownVersionKey,
                ApiKeyScope.ReadAssets,
                CancellationToken.None);

        Assert.Equal(ApiKeyErrors.InvalidCredentials.Code, result.Error?.Code);
    }

    private static ApiKeyIssuer CreateIssuer(
        FakeApiKeyStore store,
        FakeApiKeyAuditSink audit,
        byte[]? secret = null,
        DeterministicRandomSource? random = null) =>
        new(
            new FakeClock(Now),
            new FakeUuid7Generator(KeyId.Value),
            random ?? new DeterministicRandomSource(
                secret ?? Enumerable.Repeat((byte)42, 32).ToArray()),
            CreatePeppers(),
            store,
            audit);

    private static ApiKeyAuthenticator CreateAuthenticator(
        FakeApiKeyStore store,
        byte[]? pepper = null,
        IApiKeyDigestComparer? comparer = null,
        FakeApiKeyAuditSink? audit = null,
        DateTimeOffset? now = null) =>
        new(
            new FakeClock(now ?? Now),
            new ApiKeyPepperSet(
                "v1",
                new Dictionary<string, byte[]>
                {
                    ["v1"] = pepper ?? Enumerable.Repeat((byte)7, 32).ToArray(),
                }),
            store,
            comparer ?? new FixedTimeApiKeyDigestComparer(),
            audit ?? new FakeApiKeyAuditSink());

    private static ApiKeyPepperSet CreatePeppers() =>
        new(
            "v1",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)7, 32).ToArray(),
            });

    private static async Task AssertRejectedAsync(
        byte[] secret,
        DateTimeOffset? expiresAt,
        ApiKeyScope scopes,
        TenantStatus tenantStatus,
        bool revoke,
        ResultError expectedError,
        ApiKeyScope requiredScope = ApiKeyScope.ReadAssets)
    {
        var store = new FakeApiKeyStore();
        Result<IssuedApiKey> issued = await CreateIssuer(
                store,
                new FakeApiKeyAuditSink(),
                secret)
            .IssueAsync(
                new ApiKeyIssueRequest(TenantId, OwnerId, scopes, expiresAt),
                CancellationToken.None);
        Assert.True(issued.TryGetValue(out IssuedApiKey? key));
        store.TenantStatus = tenantStatus;
        if (revoke)
        {
            Assert.True(store.Added!.Revoke(Now).IsSuccess);
        }

        Result<ApiKeyPrincipal> result = await CreateAuthenticator(
                store,
                now: expectedError == ApiKeyErrors.Expired ? Now.AddSeconds(2) : Now)
            .AuthenticateAsync(
                key.PlaintextKey,
                requiredScope,
                CancellationToken.None);

        Assert.Equal(expectedError.Code, result.Error?.Code);
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeUuid7Generator(Guid value) : IUuid7Generator
    {
        public Guid NewId() => value;
    }

    private sealed class DeterministicRandomSource(byte[] bytes) : IApiKeyRandomSource
    {
        public bool WasCalled { get; private set; }

        public void Fill(Span<byte> destination)
        {
            WasCalled = true;
            bytes.CopyTo(destination);
        }
    }

    private sealed class RecordingDigestComparer : IApiKeyDigestComparer
    {
        public bool WasCalled { get; private set; }

        public int ExpectedLength { get; private set; }

        public int ActualLength { get; private set; }

        public bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
        {
            WasCalled = true;
            ExpectedLength = expected.Length;
            ActualLength = actual.Length;
            return expected.SequenceEqual(actual);
        }
    }

    private sealed class FakeApiKeyAuditSink : IApiKeyAuditSink
    {
        public List<ApiKeyAuditEvent> Events { get; } = [];

        public bool ThrowOnWrite { get; init; }

        public ValueTask WriteAsync(
            ApiKeyAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("Telemetry unavailable.");
            }

            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApiKeyStore : IApiKeyStore
    {
        public ApiKeyMetadata? Added { get; private set; }

        public TenantStatus TenantStatus { get; set; } = TenantStatus.Active;

        public int FindCalls { get; private set; }

        public int RevokeCalls { get; private set; }

        public bool ThrowOnUsage { get; set; }

        public DateTimeOffset? LastUsageAttempt { get; private set; }

        public ValueTask<Result> AddAsync(
            ApiKeyMetadata metadata,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Added = metadata;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<ApiKeyAuthenticationRecord?> FindForAuthenticationAsync(
            ApiKeyId keyId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindCalls++;
            ApiKeyAuthenticationRecord? record =
                Added is not null && Added.Id == keyId
                    ? new ApiKeyAuthenticationRecord(Added, TenantStatus)
                    : null;
            return ValueTask.FromResult(record);
        }

        public ValueTask<Result> RevokeAsync(
            TenantId tenantId,
            ApiKeyId keyId,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokeCalls++;
            if (Added is null || Added.TenantId != tenantId || Added.Id != keyId)
            {
                return ValueTask.FromResult(Result.Failure(ApiKeyErrors.NotFound));
            }

            return ValueTask.FromResult(Added.Revoke(revokedAt));
        }

        public ValueTask RecordLastUsedAsync(
            TenantId tenantId,
            ApiKeyId keyId,
            DateTimeOffset coarseUsedAt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastUsageAttempt = coarseUsedAt;
            if (ThrowOnUsage)
            {
                throw new InvalidOperationException("Telemetry unavailable.");
            }

            return ValueTask.CompletedTask;
        }

        public string CapturedText() =>
            Added is null
                ? string.Empty
                : string.Join(
                    '|',
                    Added.Id,
                    Added.TenantId,
                    Added.OwnerId,
                    Added.Prefix,
                    Added.Digest,
                    Added.Scopes,
                    Added.ExpiresAt);
    }
}
