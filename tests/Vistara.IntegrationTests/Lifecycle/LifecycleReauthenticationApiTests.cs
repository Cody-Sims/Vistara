using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Vistara.Api.Composition.Gallery;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Lifecycle;
using Vistara.Application.Common;
using Vistara.Application.Lifecycle;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Xunit;

namespace Vistara.IntegrationTests.Lifecycle;

public sealed class LifecycleReauthenticationApiTests
{
    private static readonly DateTimeOffset Now =
        new(2035, 6, 7, 8, 9, 10, TimeSpan.Zero);
    private static readonly Guid UserId =
        Guid.Parse("0199d111-1111-7111-8111-111111111111");
    private static readonly Guid TenantId =
        Guid.Parse("0199d222-2222-7222-8222-222222222222");
    private static readonly Guid AssetId =
        Guid.Parse("0199d333-3333-7333-8333-333333333333");
    private static readonly Guid BatchId =
        Guid.Parse("0199d444-4444-7444-8444-444444444444");

    [Fact]
    public async Task Forged_client_reauthentication_timestamp_is_ignored_before_batch_lookup()
    {
        var store = new RecordingLifecycleStore();
        DefaultHttpContext context = CreateContext(
            PlatformAuthenticationKind.Cookie,
            reauthentication: null);
        context.Request.Headers["X-Vistara-Authenticated-At"] =
            Now.ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture);

        await ConfirmAsync(context, store);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal(
            "lifecycle_reauthentication_required",
            await ProblemCodeAsync(context));
        Assert.Equal(0, store.ConfirmCalls);
    }

    [Fact]
    public async Task Purge_confirmation_rejects_stale_and_accepts_fresh_server_context()
    {
        var staleStore = new RecordingLifecycleStore();
        DefaultHttpContext staleContext = CreateContext(
            PlatformAuthenticationKind.Cookie,
            new PlatformReauthenticationContext(
                UserId,
                Now.AddMinutes(-6),
                PlatformAuthenticationStrength.PrimaryCredential));

        Assert.Equal(
            Now.AddMinutes(-6).ToUnixTimeSeconds().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            staleContext.User.FindFirstValue("auth_time"));
        Assert.Equal(
            PlatformAuthenticationStrength.PrimaryCredential.ToString(),
            staleContext.User.FindFirstValue("vistara_auth_strength"));
        await ConfirmAsync(staleContext, staleStore);

        Assert.Equal(StatusCodes.Status403Forbidden, staleContext.Response.StatusCode);
        Assert.Equal(
            "lifecycle_reauthentication_required",
            await ProblemCodeAsync(staleContext));
        Assert.Equal(0, staleStore.ConfirmCalls);

        var freshStore = new RecordingLifecycleStore();
        DefaultHttpContext freshContext = CreateContext(
            PlatformAuthenticationKind.Cookie,
            new PlatformReauthenticationContext(
                UserId,
                Now.AddMinutes(-5),
                PlatformAuthenticationStrength.PrimaryCredential));

        await ConfirmAsync(freshContext, freshStore);

        Assert.Equal(StatusCodes.Status202Accepted, freshContext.Response.StatusCode);
        Assert.Equal(1, freshStore.ConfirmCalls);
        Assert.Equal(UserId, freshStore.LastConfirm?.ActorId);
    }

    [Fact]
    public async Task Cookie_owner_can_create_dry_run_without_recent_reauthentication()
    {
        var store = new RecordingLifecycleStore();
        DefaultHttpContext context = CreateContext(
            PlatformAuthenticationKind.Cookie,
            reauthentication: null);
        SetJsonRequest(
            context,
            $"{{\"items\":[{{\"id\":\"{AssetId:D}\",\"version\":1}}]}}");
        context.Request.Headers["Idempotency-Key"] = "purge-dry-run";
        var service = CreateService(store);

        await LifecycleEndpoint.CreatePurgeDryRunAsync(
            context,
            CreateAuthorization(),
            service,
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, store.DryRunCalls);
        Assert.Equal(UserId, store.LastDryRun?.ActorId);
    }

    [Theory]
    [InlineData(PlatformAuthenticationKind.ApiKey)]
    [InlineData(PlatformAuthenticationKind.Bearer)]
    public async Task Non_cookie_credentials_are_explicitly_denied_purge(
        PlatformAuthenticationKind kind)
    {
        var store = new RecordingLifecycleStore();
        DefaultHttpContext context = CreateContext(kind, reauthentication: null);
        SetJsonRequest(
            context,
            $"{{\"items\":[{{\"id\":\"{AssetId:D}\",\"version\":1}}]}}");
        context.Request.Headers["Idempotency-Key"] = "purge-non-cookie";

        await LifecycleEndpoint.CreatePurgeDryRunAsync(
            context,
            CreateAuthorization(),
            CreateService(store),
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("lifecycle_forbidden", await ProblemCodeAsync(context));
        Assert.Equal(0, store.DryRunCalls);
    }

    [Fact]
    public void Reauthentication_context_must_match_the_authenticated_actor()
    {
        Guid differentActor =
            Guid.Parse("0199d111-1111-7111-8111-111111111112");

        Assert.Throws<ArgumentException>(() =>
            new PlatformIdentity(
                UserId,
                TenantId,
                "TenantOwner",
                ["metadata.manage"],
                CookieTokenCryptography.ComputeDigest("valid-csrf"),
                new PlatformReauthenticationContext(
                    differentActor,
                    Now,
                    PlatformAuthenticationStrength.PrimaryCredential)));
    }

    private static async Task ConfirmAsync(
        DefaultHttpContext context,
        RecordingLifecycleStore store)
    {
        SetJsonRequest(
            context,
            $"{{\"dryRunDigest\":\"{new string('a', 64)}\"," +
            "\"acknowledgePermanentDeletion\":true}");
        context.Request.Headers["Idempotency-Key"] = "purge-confirm";
        context.Request.Headers.IfMatch = "\"v2\"";
        await LifecycleEndpoint.ConfirmPurgeAsync(
            context,
            BatchId,
            CreateAuthorization(),
            CreateService(store),
            CancellationToken.None);
    }

    private static DefaultHttpContext CreateContext(
        PlatformAuthenticationKind kind,
        PlatformReauthenticationContext? reauthentication)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var identity = new PlatformIdentity(
            UserId,
            TenantId,
            "TenantOwner",
            ["assets.read", "metadata.manage"],
            kind == PlatformAuthenticationKind.Cookie
                ? CookieTokenCryptography.ComputeDigest("valid-csrf")
                : null,
            reauthentication);
        AuthenticateResult authentication =
            PlatformAuthenticationState.ToAuthenticateResult(
                context,
                "test",
                kind,
                PlatformCredentialResult.Success(identity));
        context.User = authentication.Principal!;
        return context;
    }

    private static GalleryLifecycleAuthorizationPort CreateAuthorization() =>
        new(new FixedTenantContext(TenantId));

    private static LifecycleService CreateService(ILifecycleStore store) =>
        new(store, new FixedClock(Now), new FixedUuid7Generator());

    private static void SetJsonRequest(DefaultHttpContext context, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = content.Length;
        context.Request.Body = new MemoryStream(content);
    }

    private static async Task<string?> ProblemCodeAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(
            context.Response.Body);
        return document.RootElement.TryGetProperty("code", out JsonElement code)
            ? code.GetString()
            : null;
    }

    private sealed class FixedTenantContext(Guid tenantId) : IPlatformTenantContext
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedUuid7Generator : IUuid7Generator
    {
        public Guid NewId() =>
            Guid.Parse("0199d555-5555-7555-8555-555555555555");
    }

    private sealed class RecordingLifecycleStore : ILifecycleStore
    {
        public int ConfirmCalls { get; private set; }
        public int DryRunCalls { get; private set; }
        public LifecycleConfirmPurgeCommand? LastConfirm { get; private set; }
        public LifecycleCreatePurgeDryRunCommand? LastDryRun { get; private set; }

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> ConfirmPurgeAsync(
            LifecycleConfirmPurgeCommand command,
            CancellationToken cancellationToken)
        {
            ConfirmCalls++;
            LastConfirm = command;
            return ValueTask.FromResult(
                Result.Success(
                    new LifecyclePurgeBatchSnapshot(
                        command.BatchId,
                        "queued",
                        Now.AddMinutes(-10),
                        Now,
                        null,
                        null,
                        1,
                        1,
                        0,
                        0,
                        [],
                        3,
                        false)));
        }

        public ValueTask<Result<LifecyclePurgeDryRunSnapshot>> CreatePurgeDryRunAsync(
            LifecycleCreatePurgeDryRunCommand command,
            CancellationToken cancellationToken)
        {
            DryRunCalls++;
            LastDryRun = command;
            return ValueTask.FromResult(
                Result.Success(
                    new LifecyclePurgeDryRunSnapshot(
                        command.BatchId,
                        "dry_run",
                        new string('a', 64),
                        command.ExpiresAtUtc,
                        1,
                        1,
                        1,
                        [],
                        1,
                        false)));
        }

        public ValueTask<Result<LifecycleTrashPage>> ListTrashAsync(
            LifecycleTrashQuery query,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<IReadOnlyList<LifecycleAssetMutationResult>>> TrashAsync(
            LifecycleTrashCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleJobSubmission>> SubmitRestoreAsync(
            LifecycleRestoreCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecyclePurgeBatchSnapshot>> GetPurgeBatchAsync(
            Guid tenantId,
            Guid actorId,
            Guid batchId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> PlaceHoldAsync(
            LifecyclePlaceHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<Result<LifecycleHoldSnapshot>> ReleaseHoldAsync(
            LifecycleReleaseHoldCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
