using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.ApiKeys;

public sealed class ApiKeyAuthenticator
{
    private readonly IClock _clock;
    private readonly IApiKeyPepperProvider _pepperProvider;
    private readonly IApiKeyStore _store;
    private readonly IApiKeyDigestComparer _digestComparer;
    private readonly IApiKeyAuditSink _auditSink;

    public ApiKeyAuthenticator(
        IClock clock,
        IApiKeyPepperProvider pepperProvider,
        IApiKeyStore store,
        IApiKeyDigestComparer digestComparer,
        IApiKeyAuditSink auditSink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _digestComparer = digestComparer ?? throw new ArgumentNullException(nameof(digestComparer));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public async ValueTask<Result<ApiKeyPrincipal>> AuthenticateAsync(
        string? plaintextKey,
        ApiKeyScope requiredScopes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        if (!ApiKeyFormat.TryParse(plaintextKey, out ParsedApiKey parsed))
        {
            await RejectAsync(null, ApiKeyErrors.InvalidCredentials, now);
            return Result.Failure<ApiKeyPrincipal>(ApiKeyErrors.InvalidCredentials);
        }

        try
        {
            if (!_pepperProvider.TryGetPepper(
                    parsed.VersionId,
                    out ReadOnlyMemory<byte> pepper))
            {
                await RejectAsync(null, ApiKeyErrors.InvalidCredentials, now);
                return Result.Failure<ApiKeyPrincipal>(ApiKeyErrors.InvalidCredentials);
            }

            ApiKeyAuthenticationRecord? record =
                await _store.FindForAuthenticationAsync(parsed.KeyId, cancellationToken);
            if (record is null ||
                record.Metadata.Id != parsed.KeyId ||
                !string.Equals(
                    record.Metadata.Prefix.Value,
                    parsed.Prefix,
                    StringComparison.Ordinal))
            {
                await RejectAsync(null, ApiKeyErrors.InvalidCredentials, now);
                return Result.Failure<ApiKeyPrincipal>(ApiKeyErrors.InvalidCredentials);
            }

            byte[] actualDigest = ApiKeyDigest.Compute(pepper.Span, parsed.Secret);
            byte[] expectedDigest = Convert.FromHexString(record.Metadata.Digest.Value);
            bool digestMatches;
            try
            {
                digestMatches = _digestComparer.Equals(expectedDigest, actualDigest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualDigest);
                CryptographicOperations.ZeroMemory(expectedDigest);
            }

            if (!digestMatches)
            {
                await RejectAsync(record, ApiKeyErrors.InvalidCredentials, now);
                return Result.Failure<ApiKeyPrincipal>(ApiKeyErrors.InvalidCredentials);
            }

            ResultError? rejection = ValidateAuthorization(record, requiredScopes, now);
            if (rejection is not null)
            {
                await RejectAsync(record, rejection, now);
                return Result.Failure<ApiKeyPrincipal>(rejection);
            }

            DateTimeOffset coarseUsedAt = new(
                now.Year,
                now.Month,
                now.Day,
                now.Hour,
                now.Minute,
                0,
                TimeSpan.Zero);
            await ApiKeyTelemetry.TryRecordUsageAsync(
                _store,
                record.Metadata.TenantId,
                record.Metadata.Id,
                coarseUsedAt);
            await ApiKeyTelemetry.TryWriteAuditAsync(
                _auditSink,
                new ApiKeyAuditEvent(
                    ApiKeyAuditAction.Authenticated,
                    record.Metadata.TenantId,
                    record.Metadata.Id,
                    null,
                    null,
                    now));
            return Result.Success(
                new ApiKeyPrincipal(
                    record.Metadata.Id,
                    record.Metadata.TenantId,
                    record.Metadata.OwnerId,
                    record.Metadata.Scopes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsed.Secret);
        }
    }

    private static ResultError? ValidateAuthorization(
        ApiKeyAuthenticationRecord record,
        ApiKeyScope requiredScopes,
        DateTimeOffset now)
    {
        if (record.TenantStatus != TenantStatus.Active)
        {
            return ApiKeyErrors.TenantInactive;
        }

        ApiKeyStatus status = record.Metadata.GetStatus(now);
        if (status == ApiKeyStatus.Revoked)
        {
            return ApiKeyErrors.Revoked;
        }

        if (status == ApiKeyStatus.Expired)
        {
            return ApiKeyErrors.Expired;
        }

        return !record.Metadata.HasScope(requiredScopes)
            ? ApiKeyErrors.InsufficientScope
            : null;
    }

    private ValueTask RejectAsync(
        ApiKeyAuthenticationRecord? record,
        ResultError error,
        DateTimeOffset now) =>
        ApiKeyTelemetry.TryWriteAuditAsync(
            _auditSink,
            new ApiKeyAuditEvent(
                ApiKeyAuditAction.AuthenticationRejected,
                record?.Metadata.TenantId,
                record?.Metadata.Id,
                null,
                error.Code,
                now));
}
