using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Delivery;

public sealed class DeliveryGrantValidator
{
    private readonly IClock _clock;
    private readonly IDeliveryGrantPepperProvider _pepperProvider;
    private readonly IDeliveryGrantStore _store;
    private readonly IDeliveryGrantAuthorizationPort _authorization;
    private readonly IDeliveryGrantDigestComparer _digestComparer;
    private readonly IDeliveryGrantAuditSink _auditSink;
    private readonly DeliveryGrantOptions _options;

    public DeliveryGrantValidator(
        IClock clock,
        IDeliveryGrantPepperProvider pepperProvider,
        IDeliveryGrantStore store,
        IDeliveryGrantAuthorizationPort authorization,
        IDeliveryGrantDigestComparer digestComparer,
        IDeliveryGrantAuditSink auditSink,
        DeliveryGrantOptions options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _digestComparer = digestComparer ?? throw new ArgumentNullException(nameof(digestComparer));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<Result<ValidatedDeliveryGrant>> ValidateAsync(
        DeliveryGrantValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        if (!DeliveryGrantTokenFormat.TryParse(
                request.PlaintextToken,
                out ParsedDeliveryGrantToken parsed) ||
            !_pepperProvider.TryGetPepper(
                parsed.PepperVersionId,
                out ReadOnlyMemory<byte> pepper))
        {
            return await RejectAsync(
                null,
                DeliveryGrantErrors.InvalidToken,
                now);
        }

        DeliveryGrantRecord? record = await _store.FindAsync(
            parsed.GrantId,
            cancellationToken);
        byte[] actualDigest = DeliveryGrantDigest.Compute(
            pepper.Span,
            request.PlaintextToken!);
        byte[] expectedDigest = ReadExpectedDigest(record);
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

        if (record is null ||
            record.GrantId != parsed.GrantId ||
            !digestMatches ||
            !string.Equals(
                record.PepperVersionId,
                parsed.PepperVersionId,
                StringComparison.Ordinal))
        {
            return await RejectAsync(
                record,
                DeliveryGrantErrors.InvalidToken,
                now);
        }

        if (record.RevokedAtUtc.HasValue)
        {
            return await RejectAsync(record, DeliveryGrantErrors.Revoked, now);
        }

        if (record.Version != parsed.GrantVersion)
        {
            return await RejectAsync(
                record,
                DeliveryGrantErrors.InvalidToken,
                now);
        }

        if (now < record.NotBeforeUtc)
        {
            return await RejectAsync(
                record,
                DeliveryGrantErrors.NotYetValid,
                now);
        }

        if (now >= record.ExpiresAtUtc)
        {
            return await RejectAsync(record, DeliveryGrantErrors.Expired, now);
        }

        if (!HasExactScope(record, request))
        {
            return await RejectAsync(
                record,
                DeliveryGrantErrors.Concealed,
                now);
        }

        DeliveryGrantAuthorizationDecision authorization =
            await _authorization.RevalidateAsync(record, cancellationToken);
        if (!authorization.IsAuthorized)
        {
            return await RejectAsync(
                record,
                authorization.IsConcealed
                    ? DeliveryGrantErrors.Concealed
                    : DeliveryGrantErrors.Forbidden,
                now);
        }

        TimeSpan maxAge = record.ExpiresAtUtc - now;
        if (maxAge > _options.MaximumPrivateCacheTtl)
        {
            maxAge = _options.MaximumPrivateCacheTtl;
        }

        var validated = new ValidatedDeliveryGrant(
            record.GrantId,
            record.Version,
            record.TenantId,
            record.Identity,
            record.Resource,
            record.Permission,
            record.ExpiresAtUtc,
            new PrivateDeliveryCachePolicy(maxAge));
        await DeliveryGrantTelemetry.TryWriteAsync(
            _auditSink,
            new DeliveryGrantAuditEvent(
                DeliveryGrantAuditAction.Validated,
                record.TenantId,
                record.GrantId,
                record.Identity.SubjectId,
                null,
                now));
        return Result.Success(validated);
    }

    private static byte[] ReadExpectedDigest(DeliveryGrantRecord? record)
    {
        if (record?.TokenDigestHex is not { Length: 64 } digestHex)
        {
            return new byte[32];
        }

        try
        {
            byte[] digest = Convert.FromHexString(digestHex);
            if (digest.Length == 32)
            {
                return digest;
            }

            CryptographicOperations.ZeroMemory(digest);
            return new byte[32];
        }
        catch (FormatException)
        {
            return new byte[32];
        }
    }

    private static bool HasExactScope(
        DeliveryGrantRecord record,
        DeliveryGrantValidationRequest request)
    {
        if (record.TenantId != request.TenantId ||
            record.Identity != request.Identity ||
            record.Resource != request.Resource ||
            record.Permission != request.RequiredAccess)
        {
            return false;
        }

        try
        {
            DeliveryGrantIssueRequest.ValidatePermission(
                record.Permission,
                record.Resource.Rendition.Kind);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async ValueTask<Result<ValidatedDeliveryGrant>> RejectAsync(
        DeliveryGrantRecord? record,
        ResultError error,
        DateTimeOffset now)
    {
        await DeliveryGrantTelemetry.TryWriteAsync(
            _auditSink,
            new DeliveryGrantAuditEvent(
                DeliveryGrantAuditAction.ValidationRejected,
                record?.TenantId,
                record?.GrantId,
                record?.Identity.SubjectId,
                error.Code,
                now));
        return Result.Failure<ValidatedDeliveryGrant>(error);
    }
}
