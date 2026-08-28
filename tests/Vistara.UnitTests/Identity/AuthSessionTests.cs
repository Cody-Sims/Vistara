using Vistara.Domain.Common;
using Vistara.Domain.Identity;

namespace Vistara.UnitTests.Identity;

public sealed class AuthSessionTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2030, 4, 5, 6, 7, 8, TimeSpan.Zero);

    [Fact]
    public void Create_stores_only_hash_metadata_and_requires_future_expiry()
    {
        Result<AuthSession> result = AuthSession.Create(
            new AuthSessionId(Guid.CreateVersion7(CreatedAt)),
            new UserId(Guid.CreateVersion7(CreatedAt.AddMilliseconds(1))),
            new SessionDigest(new string('a', 64)),
            CreatedAt,
            CreatedAt.AddHours(1));

        Assert.True(result.TryGetValue(out AuthSession? session));
        Assert.Equal(new string('a', 64), session.Digest.Value);
        Assert.Equal(SessionStatus.Active, session.GetStatus(CreatedAt));
        Assert.Equal(1, session.Version);

        Result<AuthSession> invalid = AuthSession.Create(
            new AuthSessionId(Guid.CreateVersion7(CreatedAt)),
            session.UserId,
            session.Digest,
            CreatedAt,
            CreatedAt);
        Assert.Equal("identity.session_expiry_invalid", invalid.Error?.Code);
    }

    [Fact]
    public void Revoke_is_versioned_and_duplicate_revocation_fails()
    {
        AuthSession session = CreateSession();
        DateTimeOffset revokedAt = CreatedAt.AddMinutes(1);

        Assert.True(session.Revoke(revokedAt).IsSuccess);
        Assert.Equal(revokedAt, session.RevokedAt);
        Assert.Equal(SessionStatus.Revoked, session.GetStatus(revokedAt));
        Assert.Equal(2, session.Version);

        Result duplicate = session.Revoke(revokedAt.AddMinutes(1));
        Assert.Equal("identity.session_already_revoked", duplicate.Error?.Code);
        Assert.Equal(2, session.Version);
    }

    [Fact]
    public void Expiry_is_deterministic_at_the_boundary()
    {
        AuthSession session = CreateSession();

        Assert.Equal(SessionStatus.Active, session.GetStatus(session.ExpiresAt.AddTicks(-1)));
        Assert.Equal(SessionStatus.Expired, session.GetStatus(session.ExpiresAt));
    }

    private static AuthSession CreateSession()
    {
        Result<AuthSession> result = AuthSession.Create(
            new AuthSessionId(Guid.CreateVersion7(CreatedAt)),
            new UserId(Guid.CreateVersion7(CreatedAt.AddMilliseconds(1))),
            new SessionDigest(new string('b', 64)),
            CreatedAt,
            CreatedAt.AddHours(1));
        Assert.True(result.TryGetValue(out AuthSession? session));
        return session;
    }
}
