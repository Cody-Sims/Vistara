using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;

namespace Vistara.Auth.ApiKeys;

public sealed class ApiKeyIssuer
{
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly IApiKeyRandomSource _randomSource;
    private readonly IApiKeyPepperProvider _pepperProvider;
    private readonly IApiKeyStore _store;
    private readonly IApiKeyAuditSink _auditSink;

    public ApiKeyIssuer(
        IClock clock,
        IUuid7Generator idGenerator,
        IApiKeyRandomSource randomSource,
        IApiKeyPepperProvider pepperProvider,
        IApiKeyStore store,
        IApiKeyAuditSink auditSink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public async ValueTask<Result<IssuedApiKey>> IssueAsync(
        ApiKeyIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string versionId = _pepperProvider.CurrentVersionId;
        if (!_pepperProvider.TryGetPepper(versionId, out ReadOnlyMemory<byte> pepper))
        {
            throw new InvalidOperationException(
                "The current API key pepper version has no configured secret.");
        }

        var keyId = new ApiKeyId(_idGenerator.NewId());
        string prefix = ApiKeyFormat.CreatePrefix(versionId, keyId.Value);
        byte[] secret = new byte[ApiKeyFormat.SecretByteLength];
        byte[]? digest = null;
        try
        {
            _randomSource.Fill(secret);
            digest = ApiKeyDigest.Compute(pepper.Span, secret);
            string digestHex = Convert.ToHexStringLower(digest);
            DateTimeOffset now = _clock.UtcNow;
            Result<ApiKeyMetadata> metadataResult = ApiKeyMetadata.Create(
                keyId,
                request.TenantId,
                request.OwnerId,
                prefix,
                digestHex,
                request.Scopes,
                now,
                request.ExpiresAt);
            if (!metadataResult.TryGetValue(out ApiKeyMetadata? metadata))
            {
                return Result.Failure<IssuedApiKey>(metadataResult.Error!);
            }

            Result stored = await _store.AddAsync(metadata, cancellationToken);
            if (stored.IsFailure)
            {
                return Result.Failure<IssuedApiKey>(stored.Error!);
            }

            string plaintextKey = string.Concat(
                prefix,
                "_",
                ApiKeyFormat.EncodeSecret(secret));
            await ApiKeyTelemetry.TryWriteAuditAsync(
                _auditSink,
                new ApiKeyAuditEvent(
                    ApiKeyAuditAction.Issued,
                    metadata.TenantId,
                    metadata.Id,
                    metadata.OwnerId,
                    null,
                    now));
            return Result.Success(
                new IssuedApiKey(
                    metadata.Id,
                    metadata.TenantId,
                    metadata.OwnerId,
                    metadata.Prefix.Value,
                    metadata.Scopes,
                    metadata.CreatedAt,
                    metadata.ExpiresAt,
                    plaintextKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }
}
