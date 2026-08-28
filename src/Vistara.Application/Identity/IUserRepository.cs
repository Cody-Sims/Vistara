using Vistara.Domain.Identity;

namespace Vistara.Application.Identity;

public interface IUserRepository
{
    ValueTask<User?> FindByIdAsync(UserId id, CancellationToken cancellationToken);

    ValueTask<User?> FindByEmailAsync(
        NormalizedEmail email,
        CancellationToken cancellationToken);

    ValueTask<User?> FindByLocalIdentityAsync(
        NormalizedLogin login,
        CancellationToken cancellationToken);

    ValueTask<User?> FindByExternalIdentityAsync(
        ExternalIssuer issuer,
        string subject,
        CancellationToken cancellationToken);

    ValueTask AddAsync(User user, CancellationToken cancellationToken);

    ValueTask UpdateAsync(
        User user,
        long expectedVersion,
        CancellationToken cancellationToken);
}
