using System.Security.Cryptography;
using Vistara.Application.Common;
using Vistara.Domain.Common;

namespace Vistara.Auth.Delivery;

public sealed class DeliveryGrantIssuer
{
    private readonly IClock _clock;
    private readonly IUuid7Generator _idGenerator;
    private readonly IDeliveryGrantRandomSource _randomSource;
    private readonly IDeliveryGrantPepperProvider _pepperProvider;
    private readonly IDeliveryGrantStore _store;
    private readonly IDeliveryGrantAuthorizationPort _authorization;
    private readonly IDeliveryGrantAuditSink _auditSink;
    private readonly DeliveryGrantOptions _options;

    public DeliveryGrantIssuer(
        IClock clock,
        IUuid7Generator idGenerator,
        IDeliveryGrantRandomSource randomSource,
        IDeliveryGrantPepperProvider pepperProvider,
        IDeliveryGrantStore store,
        IDeliveryGrantAuthorizationPort authorization,
        IDeliveryGrantAuditSink auditSink,
        DeliveryGrantOptions options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _randomSource = randomSource ?? throw new ArgumentNullException(nameof(randomSource));
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<Result<IssuedDeliveryGrant>> IssueAsync(
        DeliveryGrantIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _clock.UtcNow;
        if (request.NotBeforeUtc < now ||
            request.ExpiresAtUtc - now > _options.MaximumGrantTtl)
        {
            return Result.Failure<IssuedDeliveryGrant>(
                DeliveryGrantErrors.InvalidRequest);
        }

        DeliveryGrantAuthorizationDecision authorization =
            await _authorization.AuthorizeIssueAsync(request, cancellationToken);
        if (!authorization.IsAuthorized)
        {
            return Result.Failure<IssuedDeliveryGrant>(
                authorization.IsConcealed
                    ? DeliveryGrantErrors.Concealed
                    : DeliveryGrantErrors.Forbidden);
        }

        string pepperVersionId = _pepperProvider.CurrentVersionId;
        if (!_pepperProvider.TryGetPepper(
                pepperVersionId,
                out ReadOnlyMemory<byte> pepper))
        {
            throw new InvalidOperationException(
                "The current delivery grant pepper has no configured secret.");
        }

        Guid grantId = _idGenerator.NewId();
        const long grantVersion = 1;
        byte[] secret = new byte[DeliveryGrantTokenFormat.SecretByteLength];
        byte[]? digest = null;
        try
        {
            _randomSource.Fill(secret);
            string plaintextToken = DeliveryGrantTokenFormat.Create(
                pepperVersionId,
                grantId,
                grantVersion,
                secret);
            digest = DeliveryGrantDigest.Compute(pepper.Span, plaintextToken);
            var record = new DeliveryGrantRecord(
                grantId,
                grantVersion,
                request.TenantId,
                request.Identity,
                request.Resource,
                request.Permission,
                now,
                request.NotBeforeUtc,
                request.ExpiresAtUtc,
                pepperVersionId,
                Convert.ToHexStringLower(digest));
            Result stored = await _store.AddAsync(record, cancellationToken);
            if (stored.IsFailure)
            {
                return Result.Failure<IssuedDeliveryGrant>(stored.Error!);
            }

            await DeliveryGrantTelemetry.TryWriteAsync(
                _auditSink,
                new DeliveryGrantAuditEvent(
                    DeliveryGrantAuditAction.Issued,
                    record.TenantId,
                    record.GrantId,
                    record.Identity.SubjectId,
                    null,
                    now));
            return Result.Success(
                new IssuedDeliveryGrant(
                    record.GrantId,
                    record.Version,
                    record.TenantId,
                    record.Identity,
                    record.Resource,
                    record.Permission,
                    record.IssuedAtUtc,
                    record.NotBeforeUtc,
                    record.ExpiresAtUtc,
                    plaintextToken));
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
