using Vistara.Application.Common;
using Vistara.Auth.Delivery;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.DeliveryGrants;

public sealed class DeliveryGrantServicesTests
{
    private static readonly DateTimeOffset Now =
        new(2032, 3, 4, 5, 6, 7, TimeSpan.Zero);

    private static readonly Guid TenantId = Guid.CreateVersion7(Now);
    private static readonly Guid SubjectId = Guid.CreateVersion7(Now.AddMilliseconds(1));
    private static readonly Guid AssetId = Guid.CreateVersion7(Now.AddMilliseconds(2));
    private static readonly Guid RevisionId = Guid.CreateVersion7(Now.AddMilliseconds(3));
    private static readonly Guid GrantId =
        Guid.Parse("0b8e1ca2-306e-7bd3-9479-71439b417baa");

    private const string DerivativeRendition =
        "pipeline-a:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.webp";

    [Fact]
    public async Task DeliveryGrants_issue_returns_plaintext_once_and_never_persists_or_audits_it()
    {
        byte[] secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var store = new FakeDeliveryGrantStore();
        var audit = new FakeDeliveryGrantAuditSink();
        DeliveryGrantIssuer issuer = CreateIssuer(store, audit, secret);

        Result<IssuedDeliveryGrant> result = await issuer.IssueAsync(
            CreateIssueRequest(),
            CancellationToken.None);

        Assert.True(result.TryGetValue(out IssuedDeliveryGrant? issued));
        Assert.StartsWith($"vdg_v1_{GrantId:N}_1_", issued.PlaintextToken, StringComparison.Ordinal);
        Assert.NotNull(store.Added);
        Assert.Equal(64, store.Added.TokenDigestHex.Length);
        Assert.DoesNotContain(issued.PlaintextToken, store.CapturedText(), StringComparison.Ordinal);
        Assert.Single(audit.Events);
        Assert.Equal(DeliveryGrantAuditEvent.RedactedPresentedToken, audit.Events[0].PresentedToken);
        Assert.DoesNotContain(issued.PlaintextToken, audit.Events[0].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(issued.PlaintextToken, issued.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            DeliveryGrantAuditEvent.RedactedPresentedToken,
            issued.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveryGrants_validation_returns_bound_scope_and_private_cache_policy_only()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        var audit = new FakeDeliveryGrantAuditSink();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            audit,
            CreateIssueRequest(
                DeliveryGrantRendition.Derivative(DerivativeRendition),
                DeliveryGrantAccess.ReadDerivative));

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                authorization,
                audit)
            .ValidateAsync(CreateValidationRequest(issued), CancellationToken.None);

        Assert.True(result.TryGetValue(out ValidatedDeliveryGrant? validated));
        Assert.Equal(GrantId, validated.GrantId);
        Assert.Equal(DeliveryGrantAccess.ReadDerivative, validated.Access);
        Assert.True(validated.CachePolicy.IsPrivate);
        Assert.Equal(TimeSpan.FromMinutes(1), validated.CachePolicy.MaxAge);
        Assert.Equal("private, max-age=60", validated.CachePolicy.CacheControl);
        Assert.DoesNotContain(
            "key",
            string.Join('|', validated.GetType().GetProperties().Select(property => property.Name)),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, authorization.RevalidateCalls);
        Assert.DoesNotContain(
            issued.PlaintextToken,
            CreateValidationRequest(issued).ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("subject")]
    [InlineData("asset")]
    [InlineData("revision")]
    [InlineData("rendition")]
    [InlineData("permission")]
    public async Task DeliveryGrants_validation_conceals_wrong_bound_scope(string mismatch)
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest(
                DeliveryGrantRendition.Derivative(DerivativeRendition),
                DeliveryGrantAccess.ReadDerivative));
        DeliveryGrantValidationRequest request = CreateValidationRequest(issued);
        request = mismatch switch
        {
            "tenant" => request with
            {
                TenantId = Guid.CreateVersion7(Now.AddDays(1)),
            },
            "subject" => request with
            {
                Identity = new DeliveryGrantIdentity(
                    Guid.CreateVersion7(Now.AddDays(2)),
                    null,
                    null),
            },
            "asset" => request with
            {
                Resource = new DeliveryGrantResource(
                    Guid.CreateVersion7(Now.AddDays(3)),
                    request.Resource.RevisionId,
                    request.Resource.Rendition),
            },
            "revision" => request with
            {
                Resource = new DeliveryGrantResource(
                    request.Resource.AssetId,
                    Guid.CreateVersion7(Now.AddDays(4)),
                    request.Resource.Rendition),
            },
            "rendition" => request with
            {
                Resource = new DeliveryGrantResource(
                    request.Resource.AssetId,
                    request.Resource.RevisionId,
                    DeliveryGrantRendition.Derivative("other.webp")),
            },
            "permission" => request with
            {
                RequiredAccess = DeliveryGrantAccess.ReadOriginal,
            },
            _ => throw new InvalidOperationException(),
        };

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                authorization)
            .ValidateAsync(request, CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.Concealed.Code, result.Error?.Code);
    }

    [Fact]
    public async Task DeliveryGrants_never_escalate_derivative_to_original_or_metadata_access()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest(
                DeliveryGrantRendition.Derivative(DerivativeRendition),
                DeliveryGrantAccess.ReadDerivative));
        DeliveryGrantValidator validator = CreateValidator(store, authorization);

        Result<ValidatedDeliveryGrant> original = await validator.ValidateAsync(
            CreateValidationRequest(
                issued,
                resource: new DeliveryGrantResource(
                    AssetId,
                    RevisionId,
                    DeliveryGrantRendition.Original()),
                access: DeliveryGrantAccess.ReadOriginal),
            CancellationToken.None);
        Result<ValidatedDeliveryGrant> metadata = await validator.ValidateAsync(
            CreateValidationRequest(
                issued,
                resource: new DeliveryGrantResource(
                    AssetId,
                    RevisionId,
                    DeliveryGrantRendition.Metadata()),
                access: DeliveryGrantAccess.ReadMetadata),
            CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.Concealed.Code, original.Error?.Code);
        Assert.Equal(DeliveryGrantErrors.Concealed.Code, metadata.Error?.Code);
    }

    [Fact]
    public async Task DeliveryGrants_validation_checks_not_before_expiry_and_caps_cache_to_expiry()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest(expiresAtUtc: Now.AddSeconds(20)));

        Result<ValidatedDeliveryGrant> tooEarly = await CreateValidator(
                store,
                authorization,
                now: Now.AddSeconds(-1))
            .ValidateAsync(CreateValidationRequest(issued), CancellationToken.None);
        Result<ValidatedDeliveryGrant> valid = await CreateValidator(
                store,
                authorization,
                now: Now)
            .ValidateAsync(CreateValidationRequest(issued), CancellationToken.None);
        Result<ValidatedDeliveryGrant> expired = await CreateValidator(
                store,
                authorization,
                now: Now.AddSeconds(20))
            .ValidateAsync(CreateValidationRequest(issued), CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.NotYetValid.Code, tooEarly.Error?.Code);
        Assert.True(valid.TryGetValue(out ValidatedDeliveryGrant? validated));
        Assert.Equal(TimeSpan.FromSeconds(20), validated.CachePolicy.MaxAge);
        Assert.Equal(DeliveryGrantErrors.Expired.Code, expired.Error?.Code);
    }

    [Fact]
    public async Task DeliveryGrants_revocation_and_version_changes_apply_on_next_validation()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        var audit = new FakeDeliveryGrantAuditSink();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            audit,
            CreateIssueRequest());
        DeliveryGrantValidator validator = CreateValidator(store, authorization, audit);

        Assert.True((await validator.ValidateAsync(
            CreateValidationRequest(issued),
            CancellationToken.None)).IsSuccess);

        var revoker = new DeliveryGrantRevoker(
            new FakeClock(Now.AddSeconds(1)),
            store,
            audit);
        Result revoked = await revoker.RevokeAsync(
            TenantId,
            SubjectId,
            GrantId,
            1,
            CancellationToken.None);
        Result<ValidatedDeliveryGrant> afterRevoke = await validator.ValidateAsync(
            CreateValidationRequest(issued),
            CancellationToken.None);

        Assert.True(revoked.IsSuccess);
        Assert.Equal(DeliveryGrantErrors.Revoked.Code, afterRevoke.Error?.Code);
        Assert.Equal(2, store.FindCalls);
        Assert.Contains(audit.Events, item => item.Action == DeliveryGrantAuditAction.Revoked);

        store.Replace(store.Added! with { RevokedAtUtc = null, Version = 2 });
        Result<ValidatedDeliveryGrant> versionMismatch = await validator.ValidateAsync(
            CreateValidationRequest(issued),
            CancellationToken.None);
        Assert.Equal(DeliveryGrantErrors.InvalidToken.Code, versionMismatch.Error?.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-grant")]
    [InlineData("vdg_v1_not-a-guid_1_secret")]
    public async Task DeliveryGrants_validation_rejects_malformed_tokens_without_store_lookup(
        string token)
    {
        var store = new FakeDeliveryGrantStore();

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                new RecordingAuthorizationPort())
            .ValidateAsync(CreateValidationRequest(token), CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.InvalidToken.Code, result.Error?.Code);
        Assert.Equal(0, store.FindCalls);
    }

    [Fact]
    public async Task DeliveryGrants_validation_rejects_oversized_tokens_before_store_lookup()
    {
        var store = new FakeDeliveryGrantStore();
        string token = new('a', DeliveryGrantTokenLimits.MaximumPlaintextLength + 1);

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                new RecordingAuthorizationPort())
            .ValidateAsync(CreateValidationRequest(token), CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.InvalidToken.Code, result.Error?.Code);
        Assert.Equal(0, store.FindCalls);
    }

    [Fact]
    public async Task DeliveryGrants_validation_uses_constant_time_digest_comparison()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest());
        var comparer = new RecordingDigestComparer();

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                authorization,
                comparer: comparer)
            .ValidateAsync(
                CreateValidationRequest(MutateSecret(issued.PlaintextToken)),
                CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.InvalidToken.Code, result.Error?.Code);
        Assert.True(comparer.WasCalled);
        Assert.Equal(32, comparer.ExpectedLength);
        Assert.Equal(32, comparer.ActualLength);
    }

    [Fact]
    public async Task DeliveryGrants_pepper_rotation_accepts_retained_versions_and_rejects_unknown()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest());
        var rotated = new DeliveryGrantPepperSet(
            "v2",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)7, 32).ToArray(),
                ["v2"] = Enumerable.Repeat((byte)8, 32).ToArray(),
            });

        Result<ValidatedDeliveryGrant> retained = await CreateValidator(
                store,
                authorization,
                peppers: rotated)
            .ValidateAsync(CreateValidationRequest(issued), CancellationToken.None);
        string unknownToken = issued.PlaintextToken.Replace(
            "vdg_v1_",
            "vdg_v9_",
            StringComparison.Ordinal);
        Result<ValidatedDeliveryGrant> unknown = await CreateValidator(
                store,
                authorization,
                peppers: rotated)
            .ValidateAsync(CreateValidationRequest(unknownToken), CancellationToken.None);

        Assert.True(retained.IsSuccess);
        Assert.Equal(DeliveryGrantErrors.InvalidToken.Code, unknown.Error?.Code);
    }

    [Fact]
    public async Task DeliveryGrants_revalidate_active_tenant_resource_share_and_member_through_port()
    {
        var store = new FakeDeliveryGrantStore();
        var authorization = new RecordingAuthorizationPort();
        DeliveryGrantIdentity shareIdentity = new(SubjectId, GrantId, 3);
        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            new FakeDeliveryGrantAuditSink(),
            CreateIssueRequest(identity: shareIdentity));
        authorization.ValidationDecision = DeliveryGrantAuthorizationDecision.Concealed();

        Result<ValidatedDeliveryGrant> result = await CreateValidator(
                store,
                authorization)
            .ValidateAsync(
                CreateValidationRequest(issued, identity: shareIdentity),
                CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.Concealed.Code, result.Error?.Code);
        Assert.Equal(1, authorization.RevalidateCalls);
        Assert.Equal(shareIdentity, authorization.LastRevalidated?.Identity);
    }

    [Fact]
    public async Task DeliveryGrants_issue_checks_authorization_and_short_ttl_before_secret_generation()
    {
        var store = new FakeDeliveryGrantStore();
        var random = new DeterministicRandomSource(Enumerable.Repeat((byte)42, 32).ToArray());
        var authorization = new RecordingAuthorizationPort
        {
            IssueDecision = DeliveryGrantAuthorizationDecision.Concealed(),
        };
        DeliveryGrantIssuer issuer = CreateIssuer(
            store,
            new FakeDeliveryGrantAuditSink(),
            Enumerable.Repeat((byte)42, 32).ToArray(),
            random,
            authorization);

        Result<IssuedDeliveryGrant> concealed = await issuer.IssueAsync(
            CreateIssueRequest(),
            CancellationToken.None);
        authorization.IssueDecision = DeliveryGrantAuthorizationDecision.Authorized();
        Result<IssuedDeliveryGrant> tooLong = await issuer.IssueAsync(
            CreateIssueRequest(expiresAtUtc: Now.AddHours(1)),
            CancellationToken.None);

        Assert.Equal(DeliveryGrantErrors.Concealed.Code, concealed.Error?.Code);
        Assert.Equal(DeliveryGrantErrors.InvalidRequest.Code, tooLong.Error?.Code);
        Assert.False(random.WasCalled);
        Assert.Null(store.Added);
    }

    [Fact]
    public async Task DeliveryGrants_operations_honor_cancellation_and_audit_is_redacted()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var store = new FakeDeliveryGrantStore();
        var random = new DeterministicRandomSource(Enumerable.Repeat((byte)42, 32).ToArray());
        var authorization = new RecordingAuthorizationPort();
        var audit = new FakeDeliveryGrantAuditSink();
        DeliveryGrantIssuer issuer = CreateIssuer(
            store,
            audit,
            Enumerable.Repeat((byte)42, 32).ToArray(),
            random,
            authorization);
        DeliveryGrantValidator validator = CreateValidator(store, authorization, audit);
        var revoker = new DeliveryGrantRevoker(new FakeClock(Now), store, audit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await issuer.IssueAsync(CreateIssueRequest(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(
                CreateValidationRequest("anything"),
                cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await revoker.RevokeAsync(
                TenantId,
                SubjectId,
                GrantId,
                1,
                cancellation.Token));

        Assert.False(random.WasCalled);
        Assert.Null(store.Added);
        Assert.Equal(0, store.FindCalls);
        Assert.Equal(0, store.RevokeCalls);

        IssuedDeliveryGrant issued = await IssueAsync(
            store,
            authorization,
            audit,
            CreateIssueRequest());
        audit.ThrowOnWrite = true;
        Assert.True((await validator.ValidateAsync(
            CreateValidationRequest(issued),
            CancellationToken.None)).IsSuccess);
        Assert.All(
            audit.Events,
            item => Assert.Equal(
                DeliveryGrantAuditEvent.RedactedPresentedToken,
                item.PresentedToken));
        Assert.DoesNotContain(
            issued.PlaintextToken,
            string.Join('|', audit.Events),
            StringComparison.Ordinal);
    }

    private static DeliveryGrantIssueRequest CreateIssueRequest(
        DeliveryGrantRendition? rendition = null,
        DeliveryGrantAccess access = DeliveryGrantAccess.ReadOriginal,
        DateTimeOffset? expiresAtUtc = null,
        DeliveryGrantIdentity? identity = null) =>
        new(
            TenantId,
            identity ?? new DeliveryGrantIdentity(SubjectId, null, null),
            new DeliveryGrantResource(
                AssetId,
                RevisionId,
                rendition ?? DeliveryGrantRendition.Original()),
            access,
            Now,
            expiresAtUtc ?? Now.AddMinutes(5));

    private static DeliveryGrantValidationRequest CreateValidationRequest(
        IssuedDeliveryGrant issued,
        DeliveryGrantIdentity? identity = null,
        DeliveryGrantResource? resource = null,
        DeliveryGrantAccess? access = null) =>
        new(
            issued.PlaintextToken,
            issued.TenantId,
            identity ?? issued.Identity,
            resource ?? issued.Resource,
            access ?? issued.Permission);

    private static DeliveryGrantValidationRequest CreateValidationRequest(string token) =>
        new(
            token,
            TenantId,
            new DeliveryGrantIdentity(SubjectId, null, null),
            new DeliveryGrantResource(
                AssetId,
                RevisionId,
                DeliveryGrantRendition.Original()),
            DeliveryGrantAccess.ReadOriginal);

    private static async Task<IssuedDeliveryGrant> IssueAsync(
        FakeDeliveryGrantStore store,
        RecordingAuthorizationPort authorization,
        FakeDeliveryGrantAuditSink audit,
        DeliveryGrantIssueRequest request)
    {
        Result<IssuedDeliveryGrant> result = await CreateIssuer(
                store,
                audit,
                Enumerable.Repeat((byte)42, 32).ToArray(),
                authorization: authorization)
            .IssueAsync(request, CancellationToken.None);
        Assert.True(result.TryGetValue(out IssuedDeliveryGrant? issued));
        return issued;
    }

    private static DeliveryGrantIssuer CreateIssuer(
        FakeDeliveryGrantStore store,
        FakeDeliveryGrantAuditSink audit,
        byte[] secret,
        DeterministicRandomSource? random = null,
        IDeliveryGrantAuthorizationPort? authorization = null) =>
        new(
            new FakeClock(Now),
            new FakeUuid7Generator(GrantId),
            random ?? new DeterministicRandomSource(secret),
            CreatePeppers(),
            store,
            authorization ?? new RecordingAuthorizationPort(),
            audit,
            DeliveryGrantOptions.Default);

    private static DeliveryGrantValidator CreateValidator(
        FakeDeliveryGrantStore store,
        IDeliveryGrantAuthorizationPort authorization,
        FakeDeliveryGrantAuditSink? audit = null,
        DateTimeOffset? now = null,
        IDeliveryGrantDigestComparer? comparer = null,
        IDeliveryGrantPepperProvider? peppers = null) =>
        new(
            new FakeClock(now ?? Now),
            peppers ?? CreatePeppers(),
            store,
            authorization,
            comparer ?? new FixedTimeDeliveryGrantDigestComparer(),
            audit ?? new FakeDeliveryGrantAuditSink(),
            DeliveryGrantOptions.Default);

    private static DeliveryGrantPepperSet CreatePeppers() =>
        new(
            "v1",
            new Dictionary<string, byte[]>
            {
                ["v1"] = Enumerable.Repeat((byte)7, 32).ToArray(),
            });

    private static string MutateSecret(string token)
    {
        int secretIndex = token.LastIndexOf('_') + 1;
        char replacement = token[secretIndex] == 'A' ? 'B' : 'A';
        return token[..secretIndex] + replacement + token[(secretIndex + 1)..];
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FakeUuid7Generator(Guid value) : IUuid7Generator
    {
        public Guid NewId() => value;
    }

    private sealed class DeterministicRandomSource(byte[] bytes) : IDeliveryGrantRandomSource
    {
        public bool WasCalled { get; private set; }

        public void Fill(Span<byte> destination)
        {
            WasCalled = true;
            bytes.CopyTo(destination);
        }
    }

    private sealed class RecordingAuthorizationPort : IDeliveryGrantAuthorizationPort
    {
        public DeliveryGrantAuthorizationDecision IssueDecision { get; set; } =
            DeliveryGrantAuthorizationDecision.Authorized();

        public DeliveryGrantAuthorizationDecision ValidationDecision { get; set; } =
            DeliveryGrantAuthorizationDecision.Authorized();

        public int RevalidateCalls { get; private set; }

        public DeliveryGrantRecord? LastRevalidated { get; private set; }

        public ValueTask<DeliveryGrantAuthorizationDecision> AuthorizeIssueAsync(
            DeliveryGrantIssueRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(IssueDecision);
        }

        public ValueTask<DeliveryGrantAuthorizationDecision> RevalidateAsync(
            DeliveryGrantRecord grant,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevalidateCalls++;
            LastRevalidated = grant;
            return ValueTask.FromResult(ValidationDecision);
        }
    }

    private sealed class RecordingDigestComparer : IDeliveryGrantDigestComparer
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

    private sealed class FakeDeliveryGrantAuditSink : IDeliveryGrantAuditSink
    {
        public List<DeliveryGrantAuditEvent> Events { get; } = [];

        public bool ThrowOnWrite { get; set; }

        public ValueTask WriteAsync(
            DeliveryGrantAuditEvent auditEvent,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("Audit unavailable.");
            }

            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDeliveryGrantStore : IDeliveryGrantStore
    {
        public DeliveryGrantRecord? Added { get; private set; }

        public int FindCalls { get; private set; }

        public int RevokeCalls { get; private set; }

        public ValueTask<Result> AddAsync(
            DeliveryGrantRecord grant,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Added = grant;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<DeliveryGrantRecord?> FindAsync(
            Guid grantId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FindCalls++;
            return ValueTask.FromResult(
                Added?.GrantId == grantId ? Added : null);
        }

        public ValueTask<DeliveryGrantRecord?> RevokeAsync(
            Guid tenantId,
            Guid grantId,
            long expectedVersion,
            DateTimeOffset revokedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokeCalls++;
            if (Added is null ||
                Added.TenantId != tenantId ||
                Added.GrantId != grantId ||
                Added.Version != expectedVersion)
            {
                return ValueTask.FromResult<DeliveryGrantRecord?>(null);
            }

            Added = Added with
            {
                Version = Added.Version + 1,
                RevokedAtUtc = revokedAtUtc,
            };
            return ValueTask.FromResult<DeliveryGrantRecord?>(Added);
        }

        public void Replace(DeliveryGrantRecord grant) => Added = grant;

        public string CapturedText()
        {
            DeliveryGrantRecord grant = Assert.IsType<DeliveryGrantRecord>(Added);
            return string.Join(
                '|',
                grant.GrantId,
                grant.TenantId,
                grant.Identity,
                grant.Resource,
                grant.Permission,
                grant.IssuedAtUtc,
                grant.NotBeforeUtc,
                grant.ExpiresAtUtc,
                grant.TokenDigestHex,
                grant.PepperVersionId);
        }
    }
}
