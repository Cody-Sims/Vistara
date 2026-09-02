using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Azure.Core;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Vistara.Api.Composition.Gallery;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Composition.Runtime;
using Vistara.Api.OpenApi.Gallery;
using Vistara.Application.Common.Imaging;
using Vistara.Application.Common.Storage;
using Vistara.Auth.ApiKeys;
using Vistara.Auth.Cookies;
using Vistara.Auth.Oidc;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;
using Vistara.Persistence;
using Vistara.Persistence.Identity;
using Vistara.Persistence.Ingest;
using Vistara.Persistence.Model;
using Vistara.Persistence.Uploads;
using Vistara.Storage.Local;
using Vistara.Worker.Composition.Platform;
using ApiMedia = Vistara.Api.Composition.Media;
using WorkerMedia = Vistara.Worker.Composition.Media;

const string commands = "Use 'seed', 'seed-oidc', 'idp', 'serve', or 'worker'.";

if (args.Length == 0)
{
    throw new InvalidOperationException(commands);
}

if (string.Equals(args[0], "seed", StringComparison.Ordinal))
{
    await SeedAsync(ParseArguments(args[1..]));
    return;
}

if (string.Equals(args[0], "seed-oidc", StringComparison.Ordinal))
{
    await SeedOidcAsync(ParseArguments(args[1..]));
    return;
}

if (string.Equals(args[0], "idp", StringComparison.Ordinal))
{
    IReadOnlyDictionary<string, string> identityProvider = ParseArguments(args[1..]);
    await StubIdentityProvider.RunAsync(new StubIdentityProviderOptions(
        int.Parse(Required(identityProvider, "port"), CultureInfo.InvariantCulture),
        Guid.ParseExact(Required(identityProvider, "directory-tenant"), "D"),
        Required(identityProvider, "client-id"),
        Required(identityProvider, "client-secret"),
        new Uri(Required(identityProvider, "redirect-uri"), UriKind.Absolute),
        new Uri(Required(identityProvider, "post-logout-redirect-uri"), UriKind.Absolute),
        Required(identityProvider, "certificate")));
    return;
}

if (string.Equals(args[0], "worker", StringComparison.Ordinal))
{
    HostApplicationBuilder workerBuilder =
        Host.CreateApplicationBuilder(args[1..]);
    workerBuilder.Services.AddSingleton<
        WorkerMedia.IMediaRuntimeDependencies,
        TestMediaRuntimeDependencies>();
    WorkerMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
        workerBuilder.Services,
        workerBuilder.Configuration);
    workerBuilder.Services.AddVistaraWorkerPlatform(
        workerBuilder.Configuration);
    using IHost worker = workerBuilder.Build();
    worker.Services.ValidateVistaraWorkerPlatformComposition();
    await worker.RunAsync();
    return;
}

if (!string.Equals(args[0], "serve", StringComparison.Ordinal))
{
    throw new InvalidOperationException(commands);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args[1..]);
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddSingleton<
    ApiMedia.IMediaRuntimeDependencies,
    TestMediaRuntimeDependencies>();
builder.Services.AddVistaraApiRuntime(builder.Configuration);
builder.Services.AddVistaraApiPlatform(builder.Configuration);
builder.Services.AddVistaraApiPersistence(builder.Configuration);
ApiMedia.MediaServiceCollectionExtensions.AddVistaraMedia(
    builder.Services,
    builder.Configuration);
builder.Services.AddVistaraPlatformSurface();

// The stub identity provider presents a self-signed loopback certificate.
// Trusting exactly that one certificate is the only transport decision this
// suite replaces: redirects stay disabled and every other handler setting is
// the shipped one, so discovery, the key set, and the token exchange still
// travel over the API's own client.
if (Environment.GetEnvironmentVariable("VISTARA_E2E_OIDC_CERTIFICATE") is
    { Length: > 0 } trustedCertificatePath)
{
    string trustedCertificate;
    using (X509Certificate2 pinned =
        X509CertificateLoader.LoadCertificateFromFile(trustedCertificatePath))
    {
        trustedCertificate = pinned.GetCertHashString(HashAlgorithmName.SHA256);
    }

    builder.Services.AddHttpClient(OidcHttpDefaults.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            SocketsHttpHandler handler = OidcHttpDefaults.CreateHandler();
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, _, _) =>
                    certificate is not null &&
                    string.Equals(
                        certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        trustedCertificate,
                        StringComparison.OrdinalIgnoreCase);
            return handler;
        });
}

WebApplication app = builder.Build();
app.Services.ValidateVistaraApiPlatformComposition();
app.UseVistaraPlatform();
app.MapVistaraPlatformEndpoints();
app.MapVistaraPlatformSurface();
app.MapVistaraGalleryOpenApi();
app.UseStaticFiles();
app.UseVistaraSpaFallback(async context =>
{
    IFileInfo index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index, context.RequestAborted);
});
await app.RunAsync();

static async Task SeedAsync(IReadOnlyDictionary<string, string> arguments)
{
    string databasePath = Required(arguments, "database");
    string mediaRoot = Required(arguments, "media-root");
    string fixturePath = Required(arguments, "fixture");
    string statePath = Required(arguments, "state");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    Directory.CreateDirectory(mediaRoot);
    byte[] fixture = await File.ReadAllBytesAsync(fixturePath);
    string sha256 = Convert.ToHexStringLower(SHA256.HashData(fixture));
    string connectionString = $"Data Source={databasePath}";
    var databaseOptions = new DbContextOptionsBuilder<VistaraDbContext>()
        .UseSqlite(connectionString)
        .Options;
    Guid schemaTenant = Guid.CreateVersion7();
    await using (var schema = new VistaraDbContext(
                     databaseOptions,
                     new FixedTenantScope(schemaTenant)))
    {
        await schema.Database.EnsureCreatedAsync();
    }

    var localStore = new LocalBlobStore(new LocalBlobStoreOptions(mediaRoot));
    string objectKey = "e2e/seed/tiny.png";
    BlobWriteResult stored = await localStore.PutAsync(
        new BlobKey(objectKey),
        new ByteArrayBlobContent(fixture),
        new BlobWriteOptions(
            new BlobMediaType("image/png"),
            checksums:
            [
                new BlobChecksum(BlobChecksumAlgorithm.Sha256, sha256),
            ],
            conditions: BlobRequestConditions.CreateOnly),
        CancellationToken.None);

    string pepper = Required(arguments, "pepper");
    byte[] pepperBytes = Convert.FromBase64String(pepper);
    // The seed runs once per suite, so the default work factor is lowered to
    // the supported minimum rather than paid three times over.
    var passwordHasher = new Pbkdf2LocalPasswordHasher(100_000);
    var browserStates = new Dictionary<string, BrowserState>(
        StringComparer.Ordinal);
    string[] browsers = ["chromium", "firefox", "webkit"];
    for (int index = 0; index < browsers.Length; index++)
    {
        string browser = browsers[index];
        DateTimeOffset now = new(
            2026,
            8,
            1,
            12,
            index,
            0,
            TimeSpan.Zero);
        Guid tenantId = Guid.CreateVersion7(now);
        Guid userId = Guid.CreateVersion7(now.AddMilliseconds(1));
        Guid blobId = Guid.CreateVersion7(now.AddMilliseconds(2));
        Guid primaryAssetId = default;
        await using (var context = new VistaraDbContext(
                         databaseOptions,
                         new FixedTenantScope(tenantId)))
        {
            context.Tenants.Add(new TenantRow
            {
                Id = tenantId,
                TenantId = tenantId,
                Slug = $"e2e-{browser}",
                Name = $"E2E {browser}",
                Status = TenantStatus.Active.ToString(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.Users.Add(new UserRow
            {
                Id = userId,
                NormalizedEmail = $"{browser}@e2e.invalid",
                DisplayName = $"E2E {browser}",
                Status = UserStatus.Active.ToString(),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            // A password so the browser can open a cookie session, which is the
            // only credential the antiforgery contract applies to. The value
            // exists for this run only and never leaves the artifacts folder.
            Guid localIdentityId = Guid.CreateVersion7(now.AddMilliseconds(3));
            context.LocalIdentities.Add(new LocalIdentityRow
            {
                Id = localIdentityId,
                UserId = userId,
                NormalizedLogin = $"{browser}@e2e.invalid",
                LinkedAtUtc = now,
            });
            context.LocalCredentials.Add(new LocalCredentialRow
            {
                LocalIdentityId = localIdentityId,
                UserId = userId,
                PasswordHash = passwordHasher.Hash(BrowserPassword(browser)),
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.TenantMemberships.Add(new TenantMembershipRow
            {
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRole.TenantOwner.ToString(),
                Status = MembershipStatus.Active.ToString(),
                InvitedAtUtc = now,
                JoinedAtUtc = now,
                UpdatedAtUtc = now,
                Version = 1,
            });
            context.QuotaUsage.Add(new QuotaUsageRow
            {
                TenantId = tenantId,
                Version = 1,
            });
            context.Blobs.Add(new BlobRow
            {
                Id = blobId,
                TenantId = tenantId,
                Provider = localStore.Name,
                Container = "media",
                ObjectKey = objectKey,
                ProviderVersion = stored.Head.Identity.Version.Value,
                ProviderChecksum = sha256,
                Sha256 = sha256,
                SizeBytes = fixture.LongLength,
                ContentType = "image/png",
                State = "Active",
                CreatedAtUtc = now,
            });
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 INSERT INTO worker_tenant_catalog
                     (routed_tenant_id, worker_enabled, updated_at_utc, version)
                 VALUES ({tenantId}, {true}, {now}, {1L})
                 """);

            for (int assetIndex = 0; assetIndex < 30; assetIndex++)
            {
                DateTimeOffset assetTime = now.AddMinutes(-assetIndex);
                Guid assetId = Guid.CreateVersion7(
                    now.AddSeconds(assetIndex + 1));
                Guid revisionId = Guid.CreateVersion7(
                    now.AddSeconds(assetIndex + 31));
                if (assetIndex == 0)
                {
                    primaryAssetId = assetId;
                }

                var asset = new AssetRow
                {
                    Id = assetId,
                    TenantId = tenantId,
                    OwnerId = userId,
                    Title = assetIndex == 0
                        ? $"Mountain {browser}"
                        : $"Gallery item {assetIndex + 1:D2} {browser}",
                    Description = $"Safe seeded image for {browser}.",
                    Status = "Ready",
                    Visibility = "Private",
                    CapturedAtUtc = assetTime,
                    CreatedAtUtc = assetTime,
                    UpdatedAtUtc = assetTime,
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
                    DetectedFormat = "png",
                    DetectedContentType = "image/png",
                    Width = 1,
                    Height = 1,
                    FrameCount = 1,
                    SafeMetadataJson = "{}",
                    PrivateMetadataJson = "{}",
                    CreatedAtUtc = assetTime,
                });
                await context.SaveChangesAsync();
                asset.CurrentRevisionId = revisionId;
                context.AssetLifecycles.Add(new AssetLifecycleRow
                {
                    TenantId = tenantId,
                    AssetId = assetId,
                    CurrentRevision = 1,
                    State = "Ready",
                    Version = 1,
                });
                if (assetIndex == 0)
                {
                    context.AssetFavorites.Add(new AssetFavoriteRow
                    {
                        TenantId = tenantId,
                        UserId = userId,
                        AssetId = assetId,
                        AddedAtUtc = now,
                    });
                }

                await context.SaveChangesAsync();
            }

            Guid trashAssetId = Guid.CreateVersion7(now.AddSeconds(100));
            Guid trashRevisionId = Guid.CreateVersion7(now.AddSeconds(101));
            var trashAsset = new AssetRow
            {
                Id = trashAssetId,
                TenantId = tenantId,
                OwnerId = userId,
                Title = $"Deleted memory {browser}",
                Description = "A reversible E2E trash fixture.",
                Status = "Trashed",
                Visibility = "Private",
                CapturedAtUtc = now.AddDays(-2),
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now,
                Version = 2,
            };
            context.Assets.Add(trashAsset);
            await context.SaveChangesAsync();
            context.AssetRevisions.Add(new AssetRevisionRow
            {
                Id = trashRevisionId,
                TenantId = tenantId,
                AssetId = trashAssetId,
                RevisionNumber = 1,
                BlobId = blobId,
                DetectedFormat = "png",
                DetectedContentType = "image/png",
                Width = 1,
                Height = 1,
                FrameCount = 1,
                SafeMetadataJson = "{}",
                PrivateMetadataJson = "{}",
                CreatedAtUtc = now.AddDays(-2),
            });
            await context.SaveChangesAsync();
            trashAsset.CurrentRevisionId = trashRevisionId;
            context.AssetLifecycles.Add(new AssetLifecycleRow
            {
                TenantId = tenantId,
                AssetId = trashAssetId,
                CurrentRevision = 1,
                State = "Trashed",
                HasBeenTrashed = true,
                Version = 2,
            });
            context.TrashEntries.Add(new TrashEntryRow
            {
                TenantId = tenantId,
                AssetId = trashAssetId,
                DeletedByUserId = userId,
                DeletedAtUtc = now,
                PurgeAtUtc = now.AddDays(30),
                Reason = "E2E recovery workflow",
            });
            await context.SaveChangesAsync();

            string apiKey = await IssueApiKeyAsync(
                connectionString,
                tenantId,
                userId,
                pepper,
                pepperBytes,
                now);
            browserStates.Add(
                browser,
                new BrowserState(
                    tenantId,
                    userId,
                    primaryAssetId,
                    trashAssetId,
                    apiKey,
                    $"{browser}@e2e.invalid",
                    BrowserPassword(browser)));
        }
    }

    CryptographicOperations.ZeroMemory(pepperBytes);
    var state = new SeedState(browserStates);
    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
    await File.WriteAllTextAsync(
        statePath,
        JsonSerializer.Serialize(
            state,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            }));
}

/// <summary>
/// Seeds the workspace the hosted sign-in suite signs in to.
///
/// The tenant, its member, that member's password, and the directory identity
/// already linked to them are written directly, the way a deployment that has
/// been running for a while looks. The bootstrap marker is deliberately not
/// claimed: an existing workspace is not what closes first-owner bootstrap,
/// and leaving it open is what lets the same run also prove that an
/// allowlisted directory identity can claim it.
/// </summary>
static async Task SeedOidcAsync(IReadOnlyDictionary<string, string> arguments)
{
    string databasePath = Required(arguments, "database");
    string mediaRoot = Required(arguments, "media-root");
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    Directory.CreateDirectory(mediaRoot);

    Guid directoryTenantId = Guid.ParseExact(
        Required(arguments, "directory-tenant"),
        "D");
    Guid objectId = Guid.ParseExact(Required(arguments, "object-id"), "D");
    string provider = Required(arguments, "provider");
    string login = Required(arguments, "login");
    string password = Required(arguments, "password");
    string slug = Required(arguments, "tenant-slug");

    string connectionString = $"Data Source={databasePath}";
    var databaseOptions = new DbContextOptionsBuilder<VistaraDbContext>()
        .UseSqlite(connectionString)
        .Options;
    DateTimeOffset now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    Guid tenantId = Guid.CreateVersion7(now);
    Guid userId = Guid.CreateVersion7(now.AddMilliseconds(1));

    await using (var schema = new VistaraDbContext(
                     databaseOptions,
                     new FixedTenantScope(tenantId)))
    {
        await schema.Database.EnsureCreatedAsync();
    }

    await using var context = new VistaraDbContext(
        databaseOptions,
        new FixedTenantScope(tenantId));
    context.Tenants.Add(new TenantRow
    {
        Id = tenantId,
        TenantId = tenantId,
        Slug = slug,
        Name = "E2E hosted sign-in",
        Status = TenantStatus.Active.ToString(),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        Version = 1,
    });
    context.Users.Add(new UserRow
    {
        Id = userId,
        NormalizedEmail = login,
        DisplayName = "E2E member",
        Status = UserStatus.Active.ToString(),
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        Version = 1,
    });

    // The password exists so one run can show that composing hosted sign-in
    // leaves local sign-in exactly as it was. It is accepted only by the
    // throwaway database this suite creates.
    Guid localIdentityId = Guid.CreateVersion7(now.AddMilliseconds(2));
    context.LocalIdentities.Add(new LocalIdentityRow
    {
        Id = localIdentityId,
        UserId = userId,
        NormalizedLogin = login,
        LinkedAtUtc = now,
    });
    context.LocalCredentials.Add(new LocalCredentialRow
    {
        LocalIdentityId = localIdentityId,
        UserId = userId,
        PasswordHash = new Pbkdf2LocalPasswordHasher(100_000).Hash(password),
        UpdatedAtUtc = now,
        Version = 1,
    });

    // The directory identity is stored under the canonical key hosted sign-in
    // resolves users by, so the member is found rather than provisioned.
    context.ExternalIdentities.Add(new ExternalIdentityRow
    {
        Id = Guid.CreateVersion7(now.AddMilliseconds(3)),
        UserId = userId,
        Issuer = ExternalFirstOwnerCredential.CanonicalIssuer(
            provider,
            directoryTenantId),
        Subject = ExternalFirstOwnerCredential.SubjectFor(objectId),
        LinkedAtUtc = now,
    });
    context.TenantMemberships.Add(new TenantMembershipRow
    {
        TenantId = tenantId,
        UserId = userId,
        Role = TenantRole.TenantOwner.ToString(),
        Status = MembershipStatus.Active.ToString(),
        InvitedAtUtc = now,
        JoinedAtUtc = now,
        UpdatedAtUtc = now,
        Version = 1,
    });
    context.QuotaUsage.Add(new QuotaUsageRow
    {
        TenantId = tenantId,
        Version = 1,
    });
    await context.SaveChangesAsync();
}

static async Task<string> IssueApiKeyAsync(
    string connectionString,
    Guid tenantId,
    Guid userId,
    string encodedPepper,
    byte[] pepper,
    DateTimeOffset now)
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder(
        new HostApplicationBuilderSettings { DisableDefaults = true });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Persistence:Provider"] = "Sqlite",
        ["Persistence:ConnectionString"] = connectionString,
        ["Platform:Authentication:ApiKeys:CurrentPepperVersion"] = "v1",
        ["Platform:Authentication:ApiKeys:Peppers:v1"] = encodedPepper,
        ["Platform:Authentication:Jwt:Issuers:0:ProfileId"] = "e2e",
        ["Platform:Authentication:Jwt:Issuers:0:Issuer"] =
            "https://issuer.e2e.invalid",
        ["Platform:Authentication:Jwt:Issuers:0:Audience"] = "vistara-api",
        ["Platform:Authentication:Jwt:Issuers:0:MetadataAddress"] =
            "https://issuer.e2e.invalid/.well-known/openid-configuration",
        ["Platform:Authentication:Jwt:Issuers:0:AllowedAlgorithms:0"] =
            "RS256",
    });
    builder.Services.AddVistaraApiPlatform(builder.Configuration);
    PlatformServiceCollectionExtensions.AddVistaraApiPersistence(
        builder.Services,
        builder.Configuration);
    using IHost host = builder.Build();
    await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
    scope.ServiceProvider
        .GetRequiredService<IMutableTenantScope>()
        .Establish(tenantId);

    Guid keyId = Guid.CreateVersion7(now.AddMilliseconds(500));
    byte[] secret = RandomNumberGenerator.GetBytes(32);
    byte[] digest = HMACSHA256.HashData(pepper, secret);
    try
    {
        string prefix = $"vst_v1{keyId:N}";
        string plaintext = string.Concat(
            prefix,
            "_",
            Convert.ToBase64String(secret)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'));
        Result<ApiKeyMetadata> metadataResult = ApiKeyMetadata.Create(
            new ApiKeyId(keyId),
            new TenantId(tenantId),
            new UserId(userId),
            prefix,
            Convert.ToHexStringLower(digest),
            ApiKeyScope.ReadAssets |
            ApiKeyScope.UploadAssets |
            ApiKeyScope.ManageMetadata |
            ApiKeyScope.ManageApiKeys,
            now,
            now.AddYears(10));
        if (!metadataResult.TryGetValue(out ApiKeyMetadata? metadata))
        {
            throw new InvalidOperationException(
                metadataResult.Error?.Message ?? "API key creation failed.");
        }

        Result stored = await scope.ServiceProvider
            .GetRequiredService<IApiKeyStore>()
            .AddAsync(metadata, CancellationToken.None);
        if (stored.IsFailure)
        {
            throw new InvalidOperationException(
                stored.Error?.Message ?? "API key persistence failed.");
        }

        return plaintext;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(digest);
    }
}

static IReadOnlyDictionary<string, string> ParseArguments(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length ||
            !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Seed arguments must be --name value pairs.");
        }

        result.Add(values[index][2..], values[index + 1]);
    }

    return result;
}

/// <summary>
/// The sign-in password seeded for one browser's tenant. It is derived from
/// the browser name so a run is reproducible, and it is only ever accepted by
/// the throwaway database this suite creates.
/// </summary>
static string BrowserPassword(string browser) => $"E2E-{browser}-password-1";

static string Required(
    IReadOnlyDictionary<string, string> arguments,
    string name) =>
    arguments.TryGetValue(name, out string? value) &&
    !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"Missing --{name}.");

internal sealed record SeedState(
    IReadOnlyDictionary<string, BrowserState> Browsers);

internal sealed record BrowserState(
    Guid TenantId,
    Guid UserId,
    Guid PrimaryAssetId,
    Guid TrashAssetId,
    string ApiKey,
    string Login,
    string Password);

internal sealed class FixedTenantScope(Guid tenantId) : ITenantScope
{
    public Guid TenantId { get; } = tenantId;
}

internal sealed class ByteArrayBlobContent(byte[] bytes) : IReplayableBlobContent
{
    private readonly byte[] _bytes =
        bytes ?? throw new ArgumentNullException(nameof(bytes));

    public long Length => _bytes.LongLength;

    public ValueTask<Stream> OpenReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(
            new MemoryStream(_bytes, writable: false));
    }
}

internal sealed class TestMediaRuntimeDependencies :
    ApiMedia.IMediaRuntimeDependencies,
    WorkerMedia.IMediaRuntimeDependencies
{
    public AWSCredentials CreateS3Credentials(
        ApiMedia.MediaS3Options options) =>
        new AnonymousAWSCredentials();

    public TokenCredential CreateAzureCredential() =>
        throw new NotSupportedException();

    public IImageProcessor CreateImageProcessor() =>
        TestImageProcessor.Instance;

    AWSCredentials WorkerMedia.IMediaRuntimeDependencies.CreateS3Credentials(
        WorkerMedia.MediaS3Options options) =>
        new AnonymousAWSCredentials();
}

internal sealed class TestImageProcessor : IImageProcessor
{
    public static TestImageProcessor Instance { get; } = new();

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
        new("vistara-e2e-test-pipeline");

    public async ValueTask<ImageInspection> InspectAsync(
        IReplayableImageSource source,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        await using Stream input = await source.OpenReadAsync(cancellationToken);
        long bytes = 0;
        byte[] buffer = new byte[8_192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            bytes = checked(bytes + read);
        }

        return Inspection(
            ImageFormat.Png,
            "image/png",
            Math.Max(1, bytes));
    }

    public async ValueTask<ImageTransformResult> TransformAsync(
        IReplayableImageSource source,
        Stream destination,
        CanonicalTransformRecipe recipe,
        ImageDecodeLimits limits,
        CancellationToken cancellationToken)
    {
        await using Stream input = await source.OpenReadAsync(cancellationToken);
        using var content = new MemoryStream();
        await input.CopyToAsync(content, cancellationToken);
        byte[] bytes = content.ToArray();
        await destination.WriteAsync(bytes, cancellationToken);
        string contentType = recipe.OutputFormat switch
        {
            ImageFormat.Jpeg => "image/jpeg",
            ImageFormat.Png => "image/png",
            ImageFormat.WebP => "image/webp",
            _ => throw new ArgumentOutOfRangeException(nameof(recipe)),
        };
        return new ImageTransformResult(
            Inspection(recipe.OutputFormat, contentType, bytes.LongLength),
            bytes.LongLength,
            new ImageSha256(
                Convert.ToHexStringLower(SHA256.HashData(bytes))),
            recipe.Fingerprint,
            PipelineFingerprint);
    }

    private static ImageInspection Inspection(
        ImageFormat format,
        string contentType,
        long encodedBytes) =>
        new(
            format,
            new ImageMediaType(contentType),
            width: 1,
            height: 1,
            frameCount: 1,
            aggregatePixels: 1,
            ImagePixelFormat.Rgba8,
            ImageOrientation.Normal,
            new ImagePrivacyMetadata(
                HasExif: false,
                HasGps: false,
                HasXmp: false,
                HasIptc: false,
                HasComments: false,
                HasEmbeddedThumbnail: false,
                HasEmbeddedFileName: false),
            encodedBytes,
            estimatedDecodedBytes: 4);
}
