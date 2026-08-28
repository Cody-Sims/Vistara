using Vistara.Application.Common;
using Vistara.Domain.Common;
using Vistara.Domain.Identity;
using Vistara.Domain.Tenancy;

namespace Vistara.Application.Identity;

public sealed class IdentityFactory(IUuid7Generator idGenerator, IClock clock)
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IUuid7Generator _idGenerator =
        idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));

    public Result<User> CreateUser(string email, string displayName) =>
        User.Create(
            new UserId(_idGenerator.NewId()),
            email,
            displayName,
            _clock.UtcNow);

    public Result LinkLocalIdentity(User user, string login)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.LinkLocalIdentity(
            new LocalIdentityId(_idGenerator.NewId()),
            login,
            _clock.UtcNow);
    }

    public Result LinkExternalIdentity(User user, string issuer, string subject)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.LinkExternalIdentity(
            new ExternalIdentityId(_idGenerator.NewId()),
            issuer,
            subject,
            _clock.UtcNow);
    }

    public Result<AuthSession> CreateSession(
        UserId userId,
        SessionDigest digest,
        DateTimeOffset expiresAt) =>
        AuthSession.Create(
            new AuthSessionId(_idGenerator.NewId()),
            userId,
            digest,
            _clock.UtcNow,
            expiresAt);

    public Result<ApiKeyMetadata> CreateApiKeyMetadata(
        TenantId tenantId,
        UserId ownerId,
        string prefix,
        string digest,
        ApiKeyScope scopes,
        DateTimeOffset? expiresAt) =>
        ApiKeyMetadata.Create(
            new ApiKeyId(_idGenerator.NewId()),
            tenantId,
            ownerId,
            prefix,
            digest,
            scopes,
            _clock.UtcNow,
            expiresAt);
}
