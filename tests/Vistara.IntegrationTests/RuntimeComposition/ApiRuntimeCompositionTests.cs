using System.Security.Cryptography;
using Amazon.Runtime;
using Azure.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vistara.Api.Composition.Gallery;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Derivatives;
using Vistara.Api.Features.Events;
using Vistara.Api.Features.Media;
using Vistara.Api.OpenApi.Gallery;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Derivatives;
using Vistara.Application.Identity;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Jwt;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence;
using Vistara.Persistence.Auth;
using Vistara.Persistence.Derivatives;
using Vistara.Persistence.Media;
using Vistara.Persistence.Model;
using Xunit;

namespace Vistara.IntegrationTests.RuntimeComposition;

public sealed class ApiRuntimeCompositionTests
{
    [Fact]
    public void Production_composition_resolves_every_mapped_runtime_dependency()
    {
        string databasePath = CreateScratchPath("runtime.db");
        string mediaRoot = CreateScratchPath("media");
        Directory.CreateDirectory(mediaRoot);
        try
        {
            IConfiguration configuration = Configuration(databasePath, mediaRoot);
            ServiceCollection services = [];
            services.AddSingleton<IMediaRuntimeDependencies>(
                new FakeMediaRuntimeDependencies());
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            services.AddVistaraMedia(configuration);

            using ServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateScopes = true,
                });

            provider.ValidateVistaraApiPlatformComposition();
            using IServiceScope scope = provider.CreateScope();
            IServiceProvider scoped = scope.ServiceProvider;
            Assert.DoesNotContain(
                "PermitAll",
                scoped.GetRequiredService<IPlatformRateLimitHook>()
                    .GetType()
                    .Name,
                StringComparison.Ordinal);
            Assert.NotNull(scoped.GetRequiredService<ICookieSessionStore>());
            Assert.NotNull(scoped.GetRequiredService<ICookieAuthAuditSink>());
            Assert.NotNull(scoped.GetRequiredService<IApiKeyStore>());
            Assert.NotNull(scoped.GetRequiredService<IApiKeyAuditSink>());
            Assert.NotNull(scoped.GetRequiredService<IJwtTenantMembershipProvider>());
            Assert.NotNull(scoped.GetRequiredService<IJwtRevocationStore>());
            Assert.NotNull(scoped.GetRequiredService<IJwtMetadataSigningKeyResolver>());
            Assert.NotNull(scoped.GetRequiredService<IMediaDeliveryAuthorizationPort>());
            Assert.NotNull(scoped.GetRequiredService<IMediaDeliveryApplicationPort>());
            Assert.NotNull(scoped.GetRequiredService<IDerivativeAuthorizationPort>());
            Assert.NotNull(scoped.GetRequiredService<IDerivativeApplicationPort>());
            Assert.NotNull(scoped.GetRequiredService<IEventStreamAuthorizationPort>());
            Assert.NotNull(scoped.GetRequiredService<IEventStreamSource>());
        }
        finally
        {
            DeleteScratchPath(databasePath);
            DeleteScratchPath(mediaRoot);
        }
    }

    [Fact]
    public async Task Production_route_set_maps_after_complete_graph_validation()
    {
        string databasePath = CreateScratchPath("routes.db");
        string mediaRoot = CreateScratchPath("route-media");
        Directory.CreateDirectory(mediaRoot);
        try
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            foreach ((string key, string? value) in
                     ConfigurationSettings(databasePath, mediaRoot))
            {
                builder.Configuration[key] = value;
            }

            builder.Services.AddSingleton<IMediaRuntimeDependencies>(
                new FakeMediaRuntimeDependencies());
            builder.Services.AddSingleton<IPlatformRateLimitHook>(
                new DependencyFailingRateLimitHook());
            builder.Services.AddVistaraApiPlatform(builder.Configuration);
            builder.Services.AddVistaraApiPersistence(builder.Configuration);
            builder.Services.AddVistaraMedia(builder.Configuration);
            builder.Services.AddVistaraGallery(builder.Configuration);
            await using WebApplication app = builder.Build();
            app.Services.ValidateVistaraApiPlatformComposition();
            app.Services.ValidateVistaraGalleryComposition();
            app.UseVistaraPlatform();
            app.MapVistaraPlatformEndpoints();
            app.MapVistaraGalleryOpenApi();

            string[] routes = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => endpoint.RoutePattern.RawText)
                .Where(route => route is not null)
                .Cast<string>()
                .ToArray();
            Assert.Contains("/api/v1/events", routes);
            Assert.Contains("/api/v1/derivative-presets", routes);
            Assert.Contains("/api/v1/assets/{assetId:guid}/original", routes);
            Assert.Contains(
                "/media/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
                routes);
            Assert.Contains(
                "/delivery/{pipeline}/{sourceHash}/{recipeHash}.{extension}",
                routes);
            Assert.Single(routes, route => route == "/api/v1/assets");
            Assert.Single(routes, route => route == "/health/live");
            Assert.Single(routes, route => route == "/health/ready");
            Assert.Single(routes, route => route == "/health/startup");

            (int liveStatus, string liveBody) =
                await SendAsync(app, "/health/live");
            Assert.Equal(StatusCodes.Status200OK, liveStatus);
            Assert.Contains("\"name\":\"process\"", liveBody, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "\"name\":\"database\"",
                liveBody,
                StringComparison.Ordinal);

            (int readyStatus, string readyBody) =
                await SendAsync(app, "/health/ready");
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, readyStatus);
            Assert.DoesNotContain(databasePath, readyBody, StringComparison.Ordinal);
            Assert.DoesNotContain(mediaRoot, readyBody, StringComparison.Ordinal);

            (int startupStatus, string startupBody) =
                await SendAsync(app, "/health/startup");
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, startupStatus);
            Assert.DoesNotContain(databasePath, startupBody, StringComparison.Ordinal);
            Assert.DoesNotContain(mediaRoot, startupBody, StringComparison.Ordinal);
        }
        finally
        {
            DeleteScratchPath(databasePath);
            DeleteScratchPath(mediaRoot);
        }
    }

    [Fact]
    public void Production_validation_names_a_missing_mapped_dependency()
    {
        string databasePath = CreateScratchPath("runtime-missing.db");
        string mediaRoot = CreateScratchPath("media-missing");
        Directory.CreateDirectory(mediaRoot);
        try
        {
            IConfiguration configuration = Configuration(databasePath, mediaRoot);
            ServiceCollection services = [];
            services.AddSingleton<IMediaRuntimeDependencies>(
                new FakeMediaRuntimeDependencies());
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            services.AddVistaraMedia(configuration);
            services.RemoveAll<IEventStreamSource>();
            using ServiceProvider provider = services.BuildServiceProvider();

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(
                    () => provider.ValidateVistaraApiPlatformComposition());

            Assert.Contains(nameof(IEventStreamSource), error.Message);
        }
        finally
        {
            DeleteScratchPath(databasePath);
            DeleteScratchPath(mediaRoot);
        }
    }

    [Fact]
    public void Preauthentication_catalog_models_only_opaque_routing_rows()
    {
        var options =
            new DbContextOptionsBuilder<AuthenticationCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;
        using var catalog = new AuthenticationCatalogDbContext(options);

        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity =
            Assert.Single(catalog.Model.GetEntityTypes());
        Assert.Equal(
            "Vistara.Persistence.Auth.AuthenticationRouteRow",
            entity.ClrType.FullName);
        Assert.Equal(
            [
                "CreatedAtUtc",
                "CredentialId",
                "Kind",
                "LookupDigest",
                "PrincipalId",
                "RoutedTenantId",
            ],
            entity.GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public void Public_media_catalog_models_only_opaque_routing_rows()
    {
        var options =
            new DbContextOptionsBuilder<MediaCatalogDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;
        using var catalog = new MediaCatalogDbContext(options);

        Microsoft.EntityFrameworkCore.Metadata.IEntityType entity =
            Assert.Single(catalog.Model.GetEntityTypes());
        Assert.Equal(
            "Vistara.Persistence.Media.PublicDerivativeRouteRow",
            entity.ClrType.FullName);
        Assert.Equal(
            [
                "CreatedAtUtc",
                "LookupDigest",
                "RequestId",
                "RoutedTenantId",
            ],
            entity.GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task Public_media_lookup_ignores_an_unrelated_request_tenant()
    {
        string databasePath = CreateScratchPath("public-media-route.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Guid publicTenant = Guid.CreateVersion7();
        Guid unrelatedTenant = Guid.CreateVersion7();
        Guid publicUser = Guid.CreateVersion7();
        Guid requestId = Guid.CreateVersion7();
        DateTimeOffset now =
            new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        const string sourceHash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string recipeHash =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        try
        {
            await InitializeTenantAsync(
                databasePath,
                publicTenant,
                publicUser,
                "public",
                now);
            await InitializeTenantAsync(
                databasePath,
                unrelatedTenant,
                Guid.CreateVersion7(),
                "unrelated",
                now);
            (Guid assetId, Guid revisionId) =
                await AddDerivativeSourceAsync(
                    databasePath,
                    publicTenant,
                    publicUser,
                    sourceHash,
                    now);

            await RegisterPublicDerivativeAsync(
                databasePath,
                publicTenant,
                assetId,
                revisionId,
                requestId,
                sourceHash,
                recipeHash,
                now);

            IConfiguration configuration = PersistenceConfiguration(databasePath);
            ServiceCollection services = [];
            var tenantScope = new MutableTenantScope();
            tenantScope.Establish(unrelatedTenant);
            services.AddSingleton<ITenantScope>(tenantScope);
            services.AddSingleton<IMutableTenantScope>(tenantScope);
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();

            await using AsyncServiceScope lookupScope =
                provider.CreateAsyncScope();
            RelationalMediaCatalogStore lookup = lookupScope.ServiceProvider
                .GetRequiredService<RelationalMediaCatalogStore>();
            PersistedPublicDerivativeRoute? route =
                await lookup.ResolvePublicDerivativeRouteAsync(
                    "v1",
                    sourceHash,
                    recipeHash,
                    "webp",
                    CancellationToken.None);

            Assert.Equal(publicTenant, route?.TenantId);
            Assert.Equal(requestId, route?.RequestId);
            PersistedDerivativeMedia? derivative =
                await lookup.GetDerivativeAsync(
                    route!.TenantId,
                    route.RequestId,
                    CancellationToken.None);
            Assert.Equal(publicTenant, derivative?.TenantId);
            Assert.Equal(requestId, derivative?.RequestId);
            Assert.Equal(unrelatedTenant, tenantScope.TenantId);
        }
        finally
        {
            DeleteScratchPath(databasePath);
        }
    }

    private static async Task RegisterPublicDerivativeAsync(
        string databasePath,
        Guid tenantId,
        Guid assetId,
        Guid revisionId,
        Guid requestId,
        string sourceHash,
        string recipeHash,
        DateTimeOffset now)
    {
        IConfiguration configuration = PersistenceConfiguration(databasePath);
        ServiceCollection services = [];
        services.AddSingleton<ITenantScope>(new FixedTenantScope(tenantId));
        services.AddVistaraApiPlatform(configuration);
        services.AddVistaraApiPersistence(configuration);
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var source = new DerivativeSourceIdentity(
            tenantId,
            assetId,
            revisionId,
            revisionNumber: 1,
            new ImageSha256(sourceHash));
        DerivativeGenerationRequest generation =
            DerivativePresetRegistry.Standard
                .ResolveDefault(
                    source,
                    "viewer",
                    new ImagePipelineFingerprint("runtime-composition-test"))
                .GenerationRequest ??
            throw new InvalidOperationException(
                "The standard viewer derivative could not be resolved.");
        PersistedDerivativeSubmissionResult submission = await scope
            .ServiceProvider
            .GetRequiredService<RelationalDerivativeRequestStore>()
            .SubmitAsync(
                new PersistedDerivativeSubmission(
                    requestId,
                    requestId,
                    $"public-media-{requestId:N}",
                    new string('c', 64),
                    DerivativeJobContract.CreatePayload(generation),
                    isPublic: true,
                    now),
                CancellationToken.None);
        Assert.Equal(
            PersistedDerivativeSubmissionStatus.Created,
            submission.Status);
        RelationalMediaCatalogStore media = scope.ServiceProvider
            .GetRequiredService<RelationalMediaCatalogStore>();
        await media.RegisterPublicDerivativeAsync(
            tenantId,
            requestId,
            "v1",
            sourceHash,
            recipeHash,
            "webp",
            now,
            CancellationToken.None);
    }

    private static async Task<(Guid AssetId, Guid RevisionId)>
        AddDerivativeSourceAsync(
            string databasePath,
            Guid tenantId,
            Guid ownerId,
            string sourceHash,
            DateTimeOffset now)
    {
        Guid blobId = Guid.CreateVersion7();
        Guid assetId = Guid.CreateVersion7();
        Guid revisionId = Guid.CreateVersion7();
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        context.Blobs.Add(new BlobRow
        {
            Id = blobId,
            TenantId = tenantId,
            Provider = "Local",
            Container = "media",
            ObjectKey = $"originals/{blobId:N}",
            Sha256 = sourceHash,
            SizeBytes = 1,
            ContentType = "image/webp",
            State = "Active",
            CreatedAtUtc = now,
        });
        var asset = new AssetRow
        {
            Id = assetId,
            TenantId = tenantId,
            OwnerId = ownerId,
            Title = "public",
            Status = "Ready",
            Visibility = "Public",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        };
        context.Assets.Add(asset);
        await context.SaveChangesAsync();
        context.AssetRevisions.Add(new AssetRevisionRow
        {
            Id = revisionId,
            TenantId = tenantId,
            AssetId = assetId,
            RevisionNumber = 1,
            BlobId = blobId,
            DetectedFormat = "webp",
            DetectedContentType = "image/webp",
            Width = 1,
            Height = 1,
            FrameCount = 1,
            CreatedAtUtc = now,
        });
        await context.SaveChangesAsync();
        asset.CurrentRevisionId = revisionId;
        await context.SaveChangesAsync();
        return (assetId, revisionId);
    }

    [Fact]
    public async Task Unknown_api_key_route_fails_without_constructing_an_empty_tenant_key()
    {
        string databasePath = CreateScratchPath("preauth.db");
        Guid schemaTenant = Guid.CreateVersion7();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var options = new DbContextOptionsBuilder<VistaraDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using (var database = new VistaraDbContext(
                             options,
                             new FixedTenantScope(schemaTenant)))
            {
                await database.Database.EnsureCreatedAsync();
            }

            var tenantScope = new MutableTenantScope();
            ServiceCollection services = [];
            services.AddSingleton<ITenantScope>(tenantScope);
            services.AddSingleton<IMutableTenantScope>(tenantScope);
            IConfiguration configuration = PersistenceConfiguration(databasePath);
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            IApiKeyStore store =
                scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
            ApiKeyAuthenticationRecord? record =
                await store.FindForAuthenticationAsync(
                    new Vistara.Domain.Identity.ApiKeyId(Guid.CreateVersion7()),
                    CancellationToken.None);

            Assert.Null(record);
            Assert.Equal(Guid.Empty, tenantScope.TenantId);
        }
        finally
        {
            DeleteScratchPath(databasePath);
        }
    }

    [Fact]
    public async Task Api_key_routing_establishes_only_the_matching_tenant()
    {
        string databasePath = CreateScratchPath("cross-tenant.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Guid tenantOne = Guid.CreateVersion7();
        Guid tenantTwo = Guid.CreateVersion7();
        Guid userOne = Guid.CreateVersion7();
        Guid userTwo = Guid.CreateVersion7();
        Guid keyOne = Guid.CreateVersion7();
        Guid keyTwo = Guid.CreateVersion7();
        DateTimeOffset now =
            new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        try
        {
            await InitializeTenantAsync(
                databasePath,
                tenantOne,
                userOne,
                "one",
                now);
            await InitializeTenantAsync(
                databasePath,
                tenantTwo,
                userTwo,
                "two",
                now);
            IConfiguration configuration =
                PersistenceConfiguration(databasePath);
            ServiceCollection services = [];
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();

            await AddApiKeyAsync(
                provider,
                CreateApiKey(keyOne, tenantOne, userOne, '1', now));
            await AddApiKeyAsync(
                provider,
                CreateApiKey(keyTwo, tenantTwo, userTwo, '2', now));

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IApiKeyStore store =
                scope.ServiceProvider.GetRequiredService<IApiKeyStore>();
            ApiKeyAuthenticationRecord? authenticated =
                await store.FindForAuthenticationAsync(
                    new ApiKeyId(keyOne),
                    CancellationToken.None);
            Assert.Equal(tenantOne, authenticated?.Metadata.TenantId.Value);

            IApiKeyRepository repository =
                scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
            Assert.Single(await repository.ListForTenantAsync(
                new TenantId(tenantOne),
                CancellationToken.None));
            Assert.Empty(await repository.ListForTenantAsync(
                new TenantId(tenantTwo),
                CancellationToken.None));
        }
        finally
        {
            DeleteScratchPath(databasePath);
        }
    }

    [Fact]
    public async Task Wrong_api_key_digest_is_rejected_after_isolated_routing()
    {
        string databasePath = CreateScratchPath("wrong-digest.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Guid tenantId = Guid.CreateVersion7();
        Guid userId = Guid.CreateVersion7();
        Guid keyId = Guid.CreateVersion7();
        DateTimeOffset now =
            new(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        byte[] pepper = Convert.FromBase64String(
            "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=");
        byte[] correctSecret = Enumerable.Repeat((byte)1, 32).ToArray();
        byte[] wrongSecret = Enumerable.Repeat((byte)2, 32).ToArray();
        try
        {
            await InitializeTenantAsync(
                databasePath,
                tenantId,
                userId,
                "digest",
                now);
            byte[] digest = HMACSHA256.HashData(pepper, correctSecret);
            ApiKeyMetadata metadata = CreateApiKey(
                keyId,
                tenantId,
                userId,
                Convert.ToHexStringLower(digest),
                now);
            IConfiguration configuration =
                PersistenceConfiguration(databasePath);
            ServiceCollection services = [];
            services.AddVistaraApiPlatform(configuration);
            services.AddVistaraApiPersistence(configuration);
            await using ServiceProvider provider = services.BuildServiceProvider();
            await AddApiKeyAsync(provider, metadata);

            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ApiKeyAuthenticator authenticator = scope.ServiceProvider
                .GetRequiredService<ApiKeyAuthenticator>();
            Result<ApiKeyPrincipal> result = await authenticator.AuthenticateAsync(
                $"{metadata.Prefix.Value}_{Base64Url(wrongSecret)}",
                ApiKeyScope.ReadAssets,
                CancellationToken.None);

            Assert.True(result.IsFailure);
            Assert.Equal(ApiKeyErrors.InvalidCredentials.Code, result.Error?.Code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pepper);
            CryptographicOperations.ZeroMemory(correctSecret);
            CryptographicOperations.ZeroMemory(wrongSecret);
            DeleteScratchPath(databasePath);
        }
    }

    private static IConfiguration Configuration(
        string databasePath,
        string mediaRoot)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                ConfigurationSettings(databasePath, mediaRoot))
            .Build();

    private static Dictionary<string, string?> ConfigurationSettings(
        string databasePath,
        string mediaRoot)
    {
        Dictionary<string, string?> settings =
            PersistenceSettings(databasePath);
        settings["Media:Storage:Provider"] = "Local";
        settings["Media:Storage:Local:RootPath"] = mediaRoot;
        settings["Media:Imaging:Provider"] = "NetVips";
        return settings;
    }

    private static IConfiguration PersistenceConfiguration(string databasePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(PersistenceSettings(databasePath))
            .Build();

    private static Dictionary<string, string?> PersistenceSettings(
        string databasePath) =>
        new()
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = $"Data Source={databasePath}",
            ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
            ["Platform:Authentication:ApiKeys:Peppers:v1"] =
                "BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=",
            ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] =
                "runtime-composition",
            ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
                "https://issuer.example",
            ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
            ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
                "https://issuer.example/.well-known/openid-configuration",
            ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] =
                "RS256",
        };

    private static async Task InitializeTenantAsync(
        string databasePath,
        Guid tenantId,
        Guid userId,
        string slug,
        DateTimeOffset now)
    {
        var options = new DbContextOptionsBuilder<VistaraDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using var context = new VistaraDbContext(
            options,
            new FixedTenantScope(tenantId));
        await context.Database.EnsureCreatedAsync();
        context.Tenants.Add(new TenantRow
        {
            Id = tenantId,
            TenantId = tenantId,
            Slug = slug,
            Name = slug,
            Status = TenantStatus.Active.ToString(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        context.Users.Add(new UserRow
        {
            Id = userId,
            NormalizedEmail = $"{slug}@example.test",
            DisplayName = slug,
            Status = UserStatus.Active.ToString(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        context.TenantMemberships.Add(new TenantMembershipRow
        {
            TenantId = tenantId,
            UserId = userId,
            Role = TenantRole.Member.ToString(),
            Status = MembershipStatus.Active.ToString(),
            InvitedAtUtc = now,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1,
        });
        await context.SaveChangesAsync();
    }

    private static async Task AddApiKeyAsync(
        ServiceProvider provider,
        ApiKeyMetadata metadata)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        Result result = await scope.ServiceProvider
            .GetRequiredService<IApiKeyStore>()
            .AddAsync(metadata, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static ApiKeyMetadata CreateApiKey(
        Guid keyId,
        Guid tenantId,
        Guid ownerId,
        char digestCharacter,
        DateTimeOffset now)
        => CreateApiKey(
            keyId,
            tenantId,
            ownerId,
            new string(digestCharacter, 64),
            now);

    private static ApiKeyMetadata CreateApiKey(
        Guid keyId,
        Guid tenantId,
        Guid ownerId,
        string digest,
        DateTimeOffset now)
    {
        Result<ApiKeyMetadata> result = ApiKeyMetadata.Create(
            new ApiKeyId(keyId),
            new TenantId(tenantId),
            new UserId(ownerId),
            $"vst_v1{keyId:N}",
            digest,
            ApiKeyScope.ReadAssets,
            now,
            null);
        Assert.True(result.TryGetValue(out ApiKeyMetadata? metadata));
        return metadata;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static async Task<(int StatusCode, string Body)> SendAsync(
        WebApplication app,
        string path)
    {
        RequestDelegate pipeline = ((IApplicationBuilder)app).Build();
        await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await pipeline(context);
        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body)
            .ReadToEndAsync(CancellationToken.None);
        return (context.Response.StatusCode, body);
    }

    private static string CreateScratchPath(string leafName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            $"runtime-composition-{Guid.NewGuid():N}",
            leafName);

    private static void DeleteScratchPath(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class MutableTenantScope : IMutableTenantScope
    {
        public Guid TenantId { get; private set; }

        public void Establish(Guid tenantId) => TenantId = tenantId;
    }

    private sealed class FakeMediaRuntimeDependencies : IMediaRuntimeDependencies
    {
        public AWSCredentials CreateS3Credentials(MediaS3Options options) =>
            new AnonymousAWSCredentials();

        public TokenCredential CreateAzureCredential() =>
            throw new NotSupportedException();

        public IImageProcessor CreateImageProcessor() =>
            new FakeImageProcessor();
    }

    private sealed class DependencyFailingRateLimitHook : IPlatformRateLimitHook
    {
        public ValueTask<PlatformRateLimitDecision> CheckAsync(
            HttpContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Health liveness performed dependency I/O.");
    }

    private sealed class FakeImageProcessor : IImageProcessor
    {
        public ImageProcessorCapabilities Capabilities { get; } = new()
        {
            InputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            OutputFormats = [ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP],
            MaxFrames = 1,
            SupportsAutoOrientation = true,
            SupportsColorProfileNormalization = true,
            SupportsSensitiveMetadataStripping = true,
        };

        public ImagePipelineFingerprint PipelineFingerprint { get; } =
            new("runtime-composition-test");

        public ValueTask<ImageInspection> InspectAsync(
            IReplayableImageSource source,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask<ImageTransformResult> TransformAsync(
            IReplayableImageSource source,
            Stream destination,
            CanonicalTransformRecipe recipe,
            ImageDecodeLimits limits,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
