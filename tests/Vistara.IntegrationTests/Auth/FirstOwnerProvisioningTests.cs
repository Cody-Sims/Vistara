using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Features.Account;
using Vistara.Application.Common;
using Vistara.Application.Common.Auditing;
using Vistara.Application.Identity;
using Vistara.Application.Tenancy;
using Vistara.Auth.Cookies;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.Auth;

public sealed class FirstOwnerProvisioningTests
{
    private const string Password = "correct-horse-battery";

    private static readonly string[] ContentionCodes =
    [
        "setup.already_provisioned",
        "setup.provisioning_contended",
    ];

    [Fact]
    public async Task Setup_availability_closes_once_an_owner_exists()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Assert.True(await IsAvailableAsync(harness));
        _ = await harness.ProvisionAsync();
        Assert.False(await IsAvailableAsync(harness));
    }

    private static async Task<bool> IsAvailableAsync(AccountSurfaceHarness harness)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IFirstOwnerProvisioningPort>()
            .IsAvailableAsync(default);
    }

    [Fact]
    public async Task Provisioning_commits_the_whole_owner_in_one_transaction()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        await using VistaraDbContext read = harness.CreateContext(owner.TenantId);
        Assert.Equal(1, await read.Tenants.CountAsync(default));
        Assert.Equal(1, await read.Users.CountAsync(default));
        Assert.Equal(1, await read.LocalIdentities.CountAsync(default));
        Assert.Equal(1, await read.LocalCredentials.CountAsync(default));
        Assert.Equal(1, await read.AuditEvents.CountAsync(default));
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal("Active", await read.TenantMemberships
            .Where(row => row.UserId == owner.UserId)
            .Select(row => row.Status)
            .SingleAsync(default));
        string storedHash = await read.LocalCredentials
            .Select(row => row.PasswordHash)
            .SingleAsync(default);
        Assert.DoesNotContain(Password, storedHash, StringComparison.Ordinal);
        Assert.StartsWith("pbkdf2-sha256$", storedHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_attempt_with_a_different_slug_and_email_is_refused()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        ProvisionedOwnerView owner = await harness.ProvisionAsync();

        Result<ProvisionedOwnerView> second = await ProvisionAsync(
            harness,
            "second",
            "second@example.com");

        Assert.True(second.IsFailure);
        Assert.Equal("setup.already_provisioned", second.Error!.Code);
        Assert.Equal(ErrorCategory.Conflict, second.Error.Category);
        await AssertSingleOwnerAsync(harness, owner.TenantId, "acme");
    }

    [Fact]
    public async Task Concurrent_attempts_with_distinct_slugs_produce_one_winner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Task<Result<ProvisionedOwnerView>>[] attempts =
        [
            Task.Run(() => ProvisionAsync(harness, "alpha", "alpha@example.com").AsTask()),
            Task.Run(() => ProvisionAsync(harness, "bravo", "bravo@example.com").AsTask()),
            Task.Run(() => ProvisionAsync(harness, "charlie", "charlie@example.com").AsTask()),
        ];
        Result<ProvisionedOwnerView>[] results = await Task.WhenAll(attempts);

        Result<ProvisionedOwnerView> winner = Assert.Single(
            results,
            result => result.IsSuccess);
        Assert.All(
            results.Where(result => result.IsFailure),
            result =>
            {
                Assert.Equal(ErrorCategory.Conflict, result.Error!.Category);
                Assert.Contains(
                    result.Error.Code,
                    ContentionCodes,
                    StringComparer.Ordinal);
            });
        Assert.True(winner.TryGetValue(out ProvisionedOwnerView? owner));
        await AssertSingleOwnerAsync(harness, owner.TenantId, owner.TenantSlug);
    }

    [Fact]
    public async Task An_injected_failure_before_commit_rolls_back_every_row()
    {
        var guard = new ThrowingProvisioningGuard();
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ProvisionAsync(harness, "acme", "owner@example.com"));

        Assert.True(guard.Invoked);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public async Task A_cancellation_before_commit_rolls_back_every_row()
    {
        using var cancellation = new CancellationTokenSource();
        var guard = new CancellingProvisioningGuard(cancellation);
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ProvisionAsync(
                harness,
                "acme",
                "owner@example.com",
                cancellationToken: cancellation.Token));

        Assert.True(guard.Invoked);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public async Task Provisioning_remains_retryable_after_a_rolled_back_attempt()
    {
        var guard = new ThrowingProvisioningGuard();
        await using AccountSurfaceHarness harness = await AccountSurfaceHarness.CreateAsync(
            services => services.AddSingleton<IFirstOwnerProvisioningGuard>(guard));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ProvisionAsync(harness, "acme", "owner@example.com"));
        guard.Disarm();

        Result<ProvisionedOwnerView> retry =
            await ProvisionAsync(harness, "acme", "owner@example.com");

        Assert.True(retry.TryGetValue(out ProvisionedOwnerView? owner));
        await AssertSingleOwnerAsync(harness, owner.TenantId, "acme");
    }

    [Fact]
    public async Task Provisioning_rejects_a_short_password_before_writing_anything()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();

        Result<ProvisionedOwnerView> provisioned = await ProvisionAsync(
            harness,
            "acme",
            "owner@example.com",
            password: "short");

        Assert.True(provisioned.IsFailure);
        Assert.Equal("setup.weak_password", provisioned.Error!.Code);
        await AssertEmptyAsync(harness);
    }

    [Fact]
    public void Unrelated_write_failures_are_never_read_as_a_completed_bootstrap()
    {
        Assert.False(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new InvalidOperationException("boom")));
        Assert.False(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new DbUpdateException(
                    "boom",
                    new InvalidOperationException("inner"))));
        Assert.True(
            RelationalFirstOwnerProvisioningStore.IsContentionOrConstraint(
                new DbUpdateException(
                    "constraint",
                    new Microsoft.Data.Sqlite.SqliteException("unique", 19))));
    }

    [Fact]
    public async Task External_provisioning_commits_the_owner_without_a_local_password()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();

        OwnerAttempt attempt = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            directory,
            objectId);

        Assert.Equal(FirstOwnerProvisioningStatus.Provisioned, attempt.Status);
        await using VistaraDbContext read = harness.CreateContext(attempt.TenantId);
        Assert.Equal(1, await read.Tenants.CountAsync(default));
        Assert.Equal(1, await read.Users.CountAsync(default));
        Assert.Equal(1, await read.ExternalIdentities.CountAsync(default));
        Assert.Equal(0, await read.LocalIdentities.CountAsync(default));
        Assert.Equal(0, await read.LocalCredentials.CountAsync(default));
        Assert.Equal(1, await read.AuditEvents.CountAsync(default));
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal("Active", await read.TenantMemberships
            .Where(row => row.UserId == attempt.UserId)
            .Select(row => row.Status)
            .SingleAsync(default));
    }

    [Fact]
    public async Task External_identities_are_keyed_by_provider_directory_and_object_id()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();

        OwnerAttempt attempt = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            directory,
            objectId);

        await using VistaraDbContext read = harness.CreateContext(attempt.TenantId);
        ExternalIdentityRow row = await read.ExternalIdentities
            .AsNoTracking()
            .SingleAsync(default);
        Assert.Equal(attempt.UserId, row.UserId);
        Assert.Equal(EntraIssuer(directory), row.Issuer);
        Assert.Equal(objectId.ToString("D"), row.Subject);
        Assert.Contains(
            directory.ToString("D"),
            row.Issuer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "owner@example.com",
            row.Subject,
            StringComparison.OrdinalIgnoreCase);

        // Sign-in resolves the owner by the same normalized pair.
        Assert.Equal(
            row.Issuer,
            ExternalFirstOwnerCredential.NormalizeIssuer(
                EntraIssuer(directory) + "/",
                directory));
        Assert.Equal(
            row.Subject,
            ExternalFirstOwnerCredential.SubjectFor(objectId));
    }

    [Fact]
    public async Task A_local_attempt_after_an_external_owner_is_refused()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        OwnerAttempt attempt = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            Guid.NewGuid(),
            Guid.NewGuid());
        Assert.Equal(FirstOwnerProvisioningStatus.Provisioned, attempt.Status);

        Result<ProvisionedOwnerView> local = await ProvisionAsync(
            harness,
            "second",
            "second@example.com");

        Assert.True(local.IsFailure);
        Assert.Equal("setup.already_provisioned", local.Error!.Code);
        await AssertSingleOwnerAsync(harness, attempt.TenantId, "acme", localCredentials: 0);
    }

    [Fact]
    public async Task A_second_external_attempt_with_a_new_identity_is_refused()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        OwnerAttempt first = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            Guid.NewGuid(),
            Guid.NewGuid());

        OwnerAttempt second = await ProvisionExternalAsync(
            harness,
            "second",
            "second@example.com",
            Guid.NewGuid(),
            Guid.NewGuid());

        Assert.Equal(FirstOwnerProvisioningStatus.AlreadyProvisioned, second.Status);
        await using VistaraDbContext read = harness.CreateContext(first.TenantId);
        Assert.Equal(1, await read.ExternalIdentities.CountAsync(default));
        await AssertSingleOwnerAsync(harness, first.TenantId, "acme", localCredentials: 0);
    }

    [Fact]
    public async Task Concurrent_local_and_external_attempts_produce_one_winner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid sharedDirectory = Guid.NewGuid();

        Task<FirstOwnerProvisioningStatus>[] attempts =
        [
            Task.Run(async () => (await ProvisionLocalAsync(
                harness,
                "alpha",
                "alpha@example.com")).Status),
            Task.Run(async () => (await ProvisionExternalAsync(
                harness,
                "bravo",
                "bravo@example.com",
                sharedDirectory,
                Guid.NewGuid())).Status),
            Task.Run(async () => (await ProvisionExternalAsync(
                harness,
                "charlie",
                "charlie@example.com",
                sharedDirectory,
                Guid.NewGuid())).Status),
        ];
        FirstOwnerProvisioningStatus[] results = await Task.WhenAll(attempts);

        _ = Assert.Single(
            results,
            status => status == FirstOwnerProvisioningStatus.Provisioned);
        Assert.All(
            results.Where(status => status != FirstOwnerProvisioningStatus.Provisioned),
            status => Assert.True(
                status is FirstOwnerProvisioningStatus.AlreadyProvisioned
                    or FirstOwnerProvisioningStatus.Contended,
                $"Unexpected loser status {status}."));
        await AssertOneCommittedOwnerAsync(harness);
    }

    [Fact]
    public async Task Concurrent_attempts_with_the_same_external_identity_produce_one_winner()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();

        Task<OwnerAttempt>[] attempts =
        [
            Task.Run(() => ProvisionExternalAsync(
                harness, "alpha", "alpha@example.com", directory, objectId)),
            Task.Run(() => ProvisionExternalAsync(
                harness, "bravo", "bravo@example.com", directory, objectId)),
        ];
        OwnerAttempt[] results = await Task.WhenAll(attempts);

        _ = Assert.Single(
            results,
            attempt => attempt.Status == FirstOwnerProvisioningStatus.Provisioned);
        await AssertOneCommittedOwnerAsync(harness);
    }

    [Fact]
    public async Task A_duplicate_external_identity_fails_closed()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();
        await SeedForeignExternalIdentityAsync(harness, directory, objectId);

        OwnerAttempt attempt = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            directory,
            objectId);

        Assert.Equal(FirstOwnerProvisioningStatus.Contended, attempt.Status);
        await using VistaraDbContext read =
            harness.CreateContext(Guid.CreateVersion7());
        Assert.Equal(0, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(0, await read.Tenants.CountAsync(default));
        Assert.Equal(0, await read.AuditEvents.CountAsync(default));
        Assert.Equal(1, await read.Users.CountAsync(default));
        Assert.Equal(1, await read.ExternalIdentities.CountAsync(default));
    }

    [Fact]
    public async Task An_external_failure_before_commit_rolls_back_and_stays_retryable()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ProvisionExternalAsync(
                harness,
                "acme",
                "owner@example.com",
                directory,
                objectId,
                _ => throw new InvalidOperationException(
                    "Injected provisioning failure before commit.")));

        await AssertEmptyAsync(harness);
        OwnerAttempt retry = await ProvisionExternalAsync(
            harness,
            "acme",
            "owner@example.com",
            directory,
            objectId);
        Assert.Equal(FirstOwnerProvisioningStatus.Provisioned, retry.Status);
        await AssertSingleOwnerAsync(harness, retry.TenantId, "acme", localCredentials: 0);
        await using VistaraDbContext read = harness.CreateContext(retry.TenantId);
        Assert.Equal(1, await read.ExternalIdentities.CountAsync(default));
    }

    [Fact]
    public void An_external_credential_requires_a_supported_provider()
    {
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(provider: "okta"));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(provider: " "));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(provider: null!));
        ExternalFirstOwnerCredential credential =
            NewExternalCredential(provider: "  Entra  ");
        Assert.Equal(ExternalFirstOwnerProviders.Entra, credential.Provider);
    }

    [Fact]
    public void An_external_credential_requires_directory_and_object_identifiers()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => NewExternalCredential(directoryTenantId: Guid.Empty));
        Assert.ThrowsAny<ArgumentException>(
            () => NewExternalCredential(objectId: Guid.Empty));
        Assert.ThrowsAny<ArgumentException>(
            () => NewExternalCredential(externalIdentityId: Guid.Empty));
    }

    [Fact]
    public void An_external_credential_requires_an_issuer_bound_to_the_directory()
    {
        Guid directory = Guid.NewGuid();

        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(
            directoryTenantId: directory,
            issuer: "https://login.microsoftonline.com/common/v2.0"));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(
            directoryTenantId: directory,
            issuer: FormattableString.Invariant(
                $"https://login.microsoftonline.com/{Guid.NewGuid():D}/v2.0")));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(
            directoryTenantId: directory,
            issuer: FormattableString.Invariant(
                $"http://login.microsoftonline.com/{directory:D}/v2.0")));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(
            directoryTenantId: directory,
            issuer: "not-a-uri"));
        Assert.ThrowsAny<ArgumentException>(() => NewExternalCredential(
            directoryTenantId: directory,
            issuer: FormattableString.Invariant(
                $"https://login.microsoftonline.com/{directory:N}/v2.0")));
    }

    [Fact]
    public void An_external_credential_normalizes_the_issuer_and_subject()
    {
        Guid directory = Guid.NewGuid();
        Guid objectId = Guid.NewGuid();

        ExternalFirstOwnerCredential credential = NewExternalCredential(
            directoryTenantId: directory,
            objectId: objectId,
            issuer: FormattableString.Invariant(
                $"  https://LOGIN.microsoftonline.com/{directory.ToString("D").ToUpperInvariant()}/v2.0/  "));

        Assert.Equal(EntraIssuer(directory), credential.Issuer);
        Assert.Equal(objectId.ToString("D"), credential.Subject);
        Assert.Equal(directory, credential.DirectoryTenantId);
        Assert.Equal(objectId, credential.ObjectId);
    }

    [Fact]
    public async Task A_request_carries_exactly_one_authentication_factor()
    {
        await using AccountSurfaceHarness harness =
            await AccountSurfaceHarness.CreateAsync();
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        // An external owner may not also carry a local login.
        Assert.ThrowsAny<ArgumentException>(() => BuildRequest(
            services,
            "acme",
            "owner@example.com",
            (provider, user) =>
            {
                _ = provider.GetRequiredService<IdentityFactory>()
                    .LinkLocalIdentity(user, user.Email.Value);
                return NewExternalCredential();
            }));

        // A local owner's credential must belong to the owner's own login.
        Assert.ThrowsAny<ArgumentException>(() => BuildRequest(
            services,
            "acme",
            "owner@example.com",
            (provider, user) =>
            {
                _ = provider.GetRequiredService<IdentityFactory>()
                    .LinkLocalIdentity(user, user.Email.Value);
                return new LocalFirstOwnerCredential(Guid.NewGuid(), "hash");
            }));

        // A local credential must carry a password verifier.
        Assert.ThrowsAny<ArgumentException>(
            () => new LocalFirstOwnerCredential(Guid.NewGuid(), " "));
    }

    private static ValueTask<Result<ProvisionedOwnerView>> ProvisionAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        string password = Password,
        CancellationToken cancellationToken = default) =>
        ProvisionCoreAsync(harness, slug, email, password, cancellationToken);

    private static async ValueTask<Result<ProvisionedOwnerView>> ProvisionCoreAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope =
            harness.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IFirstOwnerProvisioningPort>()
            .ProvisionAsync(
                new FirstOwnerProvisioningCommand(slug, slug, email, "Owner", password),
                cancellationToken);
    }

    /// <summary>
    /// Provisions an owner whose only credential is an external directory
    /// identity, the way the hosted Entra entry point will.
    /// </summary>
    private static async Task<OwnerAttempt> ProvisionExternalAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        Guid directoryTenantId,
        Guid objectId,
        Func<CancellationToken, ValueTask>? beforeCommit = null,
        CancellationToken cancellationToken = default) =>
        await ProvisionDirectAsync(
            harness,
            slug,
            email,
            (services, _) => NewExternalCredential(
                externalIdentityId: services
                    .GetRequiredService<IUuid7Generator>()
                    .NewId(),
                directoryTenantId: directoryTenantId,
                objectId: objectId,
                issuer: EntraIssuer(directoryTenantId)),
            beforeCommit,
            cancellationToken);

    /// <summary>
    /// Provisions a local-password owner through the same store entry point the
    /// external path uses, so both factors can race each other.
    /// </summary>
    private static async Task<OwnerAttempt> ProvisionLocalAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        CancellationToken cancellationToken = default) =>
        await ProvisionDirectAsync(
            harness,
            slug,
            email,
            (services, user) =>
            {
                Assert.True(services
                    .GetRequiredService<IdentityFactory>()
                    .LinkLocalIdentity(user, user.Email.Value)
                    .IsSuccess);
                return new LocalFirstOwnerCredential(
                    user.LocalIdentities[0].Id.Value,
                    services.GetRequiredService<ILocalPasswordHasher>()
                        .Hash(Password));
            },
            beforeCommit: null,
            cancellationToken);

    private static async Task<OwnerAttempt> ProvisionDirectAsync(
        AccountSurfaceHarness harness,
        string slug,
        string email,
        Func<IServiceProvider, User, FirstOwnerCredential> credential,
        Func<CancellationToken, ValueTask>? beforeCommit,
        CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = harness.Services.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        FirstOwnerProvisioningRequest request =
            BuildRequest(services, slug, email, credential);
        FirstOwnerProvisioningStatus status = await services
            .GetRequiredService<RelationalFirstOwnerProvisioningStore>()
            .ProvisionAsync(request, beforeCommit, cancellationToken);
        return new OwnerAttempt(
            status,
            request.Tenant.Id.Value,
            request.User.Id.Value);
    }

    private static FirstOwnerProvisioningRequest BuildRequest(
        IServiceProvider services,
        string slug,
        string email,
        Func<IServiceProvider, User, FirstOwnerCredential> credential)
    {
        var tenants = services.GetRequiredService<TenantFactory>();
        var identities = services.GetRequiredService<IdentityFactory>();
        var ids = services.GetRequiredService<IUuid7Generator>();
        Assert.True(tenants.Create(slug, slug).TryGetValue(out Tenant? tenant));
        Assert.True(identities.CreateUser(email, "Owner").TryGetValue(out User? user));
        Assert.True(tenants
            .InviteMember(tenant!.Id, user!.Id, TenantRole.TenantOwner)
            .TryGetValue(out TenantMembership? membership));
        DateTimeOffset now = services.GetRequiredService<IClock>().UtcNow;
        Assert.True(membership!.Activate(now).IsSuccess);
        var audit = new AuditRecord(
            new AuditEventId(ids.NewId()),
            new AuditTenantId(tenant.Id.Value),
            new AuditActor(AuditActorKind.System, "first-owner-provisioning"),
            "tenant.owner.provisioned",
            new AuditResource("tenant", tenant.Id.Value.ToString("D")),
            AuditChangeSummary.Empty,
            AuditChangeSummary.Empty,
            AuditOutcome.Succeeded,
            now);
        return new FirstOwnerProvisioningRequest(
            tenant,
            user,
            membership,
            credential(services, user),
            audit);
    }

    private static ExternalFirstOwnerCredential NewExternalCredential(
        Guid? externalIdentityId = null,
        string provider = ExternalFirstOwnerProviders.Entra,
        Guid? directoryTenantId = null,
        Guid? objectId = null,
        string? issuer = null)
    {
        Guid directory = directoryTenantId ?? Guid.NewGuid();
        return new ExternalFirstOwnerCredential(
            externalIdentityId ?? Guid.CreateVersion7(),
            provider,
            issuer ?? EntraIssuer(directory),
            directory,
            objectId ?? Guid.NewGuid());
    }

    private static string EntraIssuer(Guid directoryTenantId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"https://login.microsoftonline.com/{directoryTenantId:D}/v2.0");

    /// <summary>
    /// Plants an external identity that already belongs to another user so the
    /// bootstrap must fail closed rather than take over the identity.
    /// </summary>
    private static async Task SeedForeignExternalIdentityAsync(
        AccountSurfaceHarness harness,
        Guid directoryTenantId,
        Guid objectId)
    {
        await using VistaraDbContext write =
            harness.CreateContext(Guid.CreateVersion7());
        Guid userId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        write.Users.Add(new UserRow
        {
            Id = userId,
            NormalizedEmail = "planted@example.com",
            DisplayName = "Planted",
            Status = "Active",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        write.ExternalIdentities.Add(new ExternalIdentityRow
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Issuer = EntraIssuer(directoryTenantId),
            Subject = objectId.ToString("D"),
            LinkedAtUtc = now,
        });
        _ = await write.SaveChangesAsync(default);
    }

    private static async Task AssertEmptyAsync(AccountSurfaceHarness harness)
    {
        await using VistaraDbContext read =
            harness.CreateContext(Guid.CreateVersion7());
        Assert.Equal(0, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(0, await read.Users.CountAsync(default));
        Assert.Equal(0, await read.LocalIdentities.CountAsync(default));
        Assert.Equal(0, await read.LocalCredentials.CountAsync(default));
        Assert.Equal(0, await read.ExternalIdentities.CountAsync(default));
        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        Assert.Equal(0, await catalog.Tenants.CountAsync(default));
        Assert.Equal(0, await catalog.TenantMemberships.CountAsync(default));
    }

    /// <summary>
    /// Asserts that exactly one bootstrap winner is committed, whichever factor
    /// and slug won the race.
    /// </summary>
    private static async Task AssertOneCommittedOwnerAsync(
        AccountSurfaceHarness harness)
    {
        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        Assert.Equal(1, await catalog.Tenants.CountAsync(default));
        Assert.Equal(1, await catalog.Users.CountAsync(default));
        Assert.Equal(1, await catalog.TenantMemberships.CountAsync(default));
        Guid tenantId = await catalog.Tenants
            .Select(row => row.Id.Value)
            .SingleAsync(default);
        await using VistaraDbContext read = harness.CreateContext(tenantId);
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(
            1,
            await read.LocalCredentials.CountAsync(default) +
            await read.ExternalIdentities.CountAsync(default));
    }

    private static async Task AssertSingleOwnerAsync(
        AccountSurfaceHarness harness,
        Guid tenantId,
        string slug,
        int localCredentials = 1)
    {
        await using IdentityCatalogDbContext catalog = harness.CreateCatalog();
        Assert.Equal(1, await catalog.Tenants.CountAsync(default));
        Assert.Equal(1, await catalog.Users.CountAsync(default));
        Assert.Equal(localCredentials, await catalog.LocalCredentials.CountAsync(default));
        Assert.Equal(1, await catalog.TenantMemberships.CountAsync(default));
        Assert.Equal(
            slug,
            await catalog.Tenants.Select(row => row.Slug).SingleAsync(default));
        await using VistaraDbContext read = harness.CreateContext(tenantId);
        Assert.Equal(1, await read.PlatformBootstrap.CountAsync(default));
        Assert.Equal(
            tenantId,
            await read.PlatformBootstrap
                .Select(row => row.OwnerTenantId)
                .SingleAsync(default));
    }

    private sealed class ThrowingProvisioningGuard : IFirstOwnerProvisioningGuard
    {
        private bool _armed = true;

        public bool Invoked { get; private set; }

        public void Disarm() => _armed = false;

        public ValueTask BeforeCommitAsync(CancellationToken cancellationToken)
        {
            if (!_armed)
            {
                return ValueTask.CompletedTask;
            }

            Invoked = true;
            throw new InvalidOperationException(
                "Injected provisioning failure before commit.");
        }
    }

    private sealed class CancellingProvisioningGuard(CancellationTokenSource source)
        : IFirstOwnerProvisioningGuard
    {
        public bool Invoked { get; private set; }

        public async ValueTask BeforeCommitAsync(CancellationToken cancellationToken)
        {
            Invoked = true;
            await source.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The outcome of one direct store attempt and the rows it claimed.</summary>
    private sealed record OwnerAttempt(
        FirstOwnerProvisioningStatus Status,
        Guid TenantId,
        Guid UserId);
}
