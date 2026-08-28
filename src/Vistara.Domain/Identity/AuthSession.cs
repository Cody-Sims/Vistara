using Vistara.Domain.Common;

namespace Vistara.Domain.Identity;

public sealed class AuthSession
{
    private AuthSession(
        AuthSessionId id,
        UserId userId,
        SessionDigest digest,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        Digest = digest;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public AuthSessionId Id { get; }

    public UserId UserId { get; }

    public SessionDigest Digest { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    public static Result<AuthSession> Create(
        AuthSessionId id,
        UserId userId,
        SessionDigest digest,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (createdAt.Offset != TimeSpan.Zero || expiresAt.Offset != TimeSpan.Zero)
        {
            return Result.Failure<AuthSession>(IdentityErrors.TimestampNotUtc);
        }

        if (expiresAt <= createdAt)
        {
            return Result.Failure<AuthSession>(IdentityErrors.SessionExpiryInvalid);
        }

        return Result.Success(new AuthSession(id, userId, digest, createdAt, expiresAt));
    }

    public SessionStatus GetStatus(DateTimeOffset at)
    {
        if (at.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                IdentityErrors.TimestampNotUtc.Message,
                nameof(at));
        }

        if (RevokedAt.HasValue)
        {
            return SessionStatus.Revoked;
        }

        return at >= ExpiresAt
            ? SessionStatus.Expired
            : SessionStatus.Active;
    }

    public Result Revoke(DateTimeOffset revokedAt)
    {
        Result timestampResult = ValidateMutationTimestamp(revokedAt);
        if (timestampResult.IsFailure)
        {
            return timestampResult;
        }

        if (RevokedAt.HasValue)
        {
            return Result.Failure(IdentityErrors.SessionAlreadyRevoked);
        }

        RevokedAt = revokedAt;
        UpdatedAt = revokedAt;
        Version++;
        return Result.Success();
    }

    private Result ValidateMutationTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp.Offset != TimeSpan.Zero)
        {
            return Result.Failure(IdentityErrors.TimestampNotUtc);
        }

        return timestamp < UpdatedAt
            ? Result.Failure(IdentityErrors.TimestampOutOfOrder)
            : Result.Success();
    }
}
