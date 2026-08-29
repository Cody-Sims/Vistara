using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Auth.ApiKeys;

public sealed class ApiKeyRevoker
{
    private readonly IClock _clock;
    private readonly IApiKeyStore _store;
    private readonly IApiKeyAuditSink _auditSink;

    public ApiKeyRevoker(
        IClock clock,
        IApiKeyStore store,
        IApiKeyAuditSink auditSink)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public async ValueTask<Result> RevokeAsync(
        TenantId tenantId,
        UserId actorUserId,
        ApiKeyId keyId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        Result result = await _store.RevokeAsync(
            tenantId,
            keyId,
            now,
            cancellationToken);
        if (result.IsFailure)
        {
            return result;
        }

        await ApiKeyTelemetry.TryWriteAuditAsync(
            _auditSink,
            new ApiKeyAuditEvent(
                ApiKeyAuditAction.Revoked,
                tenantId,
                keyId,
                actorUserId,
                null,
                now));
        return Result.Success();
    }
}
