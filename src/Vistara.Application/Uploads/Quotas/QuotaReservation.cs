using Vistara.Domain.Tenancy;

namespace Vistara.Application.Uploads.Quotas;

public enum QuotaReservationState
{
    Reserved,
    Consumed,
    Released,
    Expired,
}

public sealed record QuotaReservation
{
    private QuotaReservation(
        Guid id,
        TenantId tenantId,
        string idempotencyKey,
        string requestFingerprint,
        QuotaAmounts amount,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        QuotaReservationState state,
        long version)
    {
        Id = id;
        TenantId = tenantId;
        IdempotencyKey = idempotencyKey;
        RequestFingerprint = requestFingerprint;
        Amount = amount;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        State = state;
        Version = version;
    }

    public Guid Id { get; }

    public TenantId TenantId { get; }

    public string IdempotencyKey { get; }

    public string RequestFingerprint { get; }

    public QuotaAmounts Amount { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public QuotaReservationState State { get; }

    public long Version { get; }

    public static QuotaReservation Create(
        Guid id,
        TenantId tenantId,
        string idempotencyKey,
        string requestFingerprint,
        QuotaAmounts amount,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        EnsureUuid7(id, nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        EnsureUtc(createdAtUtc, nameof(createdAtUtc));
        EnsureUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "Reservation expiry must follow creation.");
        }

        return new QuotaReservation(
            id,
            tenantId,
            idempotencyKey.Trim(),
            requestFingerprint.Trim(),
            amount,
            createdAtUtc,
            expiresAtUtc,
            QuotaReservationState.Reserved,
            1);
    }

    public QuotaReservation Transition(QuotaReservationState target)
    {
        if (State != QuotaReservationState.Reserved ||
            target == QuotaReservationState.Reserved ||
            !Enum.IsDefined(target))
        {
            throw new InvalidOperationException("The quota reservation transition is invalid.");
        }

        return new QuotaReservation(
            Id,
            TenantId,
            IdempotencyKey,
            RequestFingerprint,
            Amount,
            CreatedAtUtc,
            ExpiresAtUtc,
            target,
            checked(Version + 1));
    }

    private static void EnsureUuid7(Guid value, string parameterName)
    {
        if (value == Guid.Empty || value.Version != 7)
        {
            throw new ArgumentException("Reservation IDs must be UUIDv7.", parameterName);
        }
    }

    private static void EnsureUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must use UTC.", parameterName);
        }
    }
}
